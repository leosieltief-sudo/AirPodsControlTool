using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using AirPodsControl.Core.Services;
using Microsoft.Win32.SafeHandles;

namespace AirPodsControl.Windows;

public enum ControlChannelAvailability
{
    /// <summary>A kernel profile driver exposes an L2CAP PSM 0x1001 pipe for this device.</summary>
    Available,
    /// <summary>No driver is bound to the AirPods "AAP Server" service node: Windows offers no user-mode L2CAP.</summary>
    NoDriver,
    /// <summary>MagicAAP is installed but no device interface for this AirPods is present (device disconnected / driver not started).</summary>
    DriverInstalledNoInterface,
    /// <summary>The paired device does not expose an AAP service node.</summary>
    NoAapService,
}

public sealed record ControlChannelProbe(ControlChannelAvailability Availability, string? InterfacePath, string Explanation);

/// <summary>
/// Why a driver is needed: AirPods speak AAP over L2CAP PSM 0x1001. Windows exposes classic-Bluetooth L2CAP only to
/// kernel-mode profile drivers (BRB_L2CA_OPEN_CHANNEL); Winsock AF_BTH supports RFCOMM only and WinRT has no BR/EDR
/// L2CAP API. The only existing driver that bridges this for AirPods is MagicAAP (by the MagicPods author).
/// Its interface GUID is taken from YimingZhanshen/AirPodsWindows (Source/Core/MagicAAPWinRT.cpp), which uses it as a
/// raw byte pipe (WriteFile/ReadFile, no framing). This path REQUIRES HARDWARE VERIFICATION.
/// </summary>
public static class AapControlChannel
{
    public static readonly Guid MagicAapInterfaceGuid = new("9EEC98BB-3C54-45D4-A843-7900C4635E08");
    public const string MagicAapService = "MagicAAP";

    public static bool IsMagicAapInstalled => Native.IsServiceInstalled(MagicAapService);

    public static ControlChannelProbe Probe(PairedAppleDevice device)
    {
        if (device.AapNodeInstanceId is null)
            return new(ControlChannelAvailability.NoAapService, null, "This device does not publish the AAP service; noise control is not available.");

        // 1) interfaces registered on this device's own AAP node (precise)
        var paths = Native.GetDeviceInterfaces(MagicAapInterfaceGuid, device.AapNodeInstanceId).ToList();
        // 2) otherwise any MagicAAP interface whose path contains this MAC (never "first device" for safety)
        if (paths.Count == 0)
            paths = Native.GetDeviceInterfaces(MagicAapInterfaceGuid, null)
                .Where(p => p.Contains(device.MacHex, StringComparison.OrdinalIgnoreCase)).ToList();

        if (paths.Count > 0)
            return new(ControlChannelAvailability.Available, paths[0], $"L2CAP 0x1001 via driver '{device.AapNodeDriver ?? MagicAapService}'.");

        if (IsMagicAapInstalled || string.Equals(device.AapNodeDriver, MagicAapService, StringComparison.OrdinalIgnoreCase))
            return new(ControlChannelAvailability.DriverInstalledNoInterface, null,
                "MagicAAP driver is installed but exposes no channel for these AirPods (connect them, or check Device Manager → 'AAP Server').");

        return new(ControlChannelAvailability.NoDriver, null,
            "Windows does not allow apps to open the AirPods control channel (L2CAP PSM 0x1001) without a Bluetooth profile driver. " +
            "Battery is read via BLE; for ANC / Transparency / Off install the MagicAAP driver (see README).");
    }

    /// <summary>
    /// Diagnostic: confirm that the in-box Winsock provider refuses L2CAP sockets on this machine.
    /// Returns the WSA error text (expected: protocol/socket type not supported).
    /// </summary>
    public static string ProbeWinsockL2cap()
    {
        const int AF_BTH = 32, BTHPROTO_L2CAP = 0x0100;
        try
        {
            using var s = new Socket((AddressFamily)AF_BTH, SocketType.Seqpacket, (ProtocolType)BTHPROTO_L2CAP);
            return "UNEXPECTED: Winsock created an AF_BTH/L2CAP socket.";
        }
        catch (SocketException ex) { return $"refused: {ex.SocketErrorCode} ({ex.ErrorCode})"; }
        catch (Exception ex) { return $"refused: {ex.GetType().Name}: {ex.Message}"; }
    }
}

/// <summary>AAP transport over a driver-provided device interface (one ReadFile == one L2CAP SDU).</summary>
public sealed class DriverAapTransport : IAapTransport
{
    private readonly string _path;
    private readonly ILog _log;
    private FileStream? _stream;
    private CancellationTokenSource? _readCts;
    private int _closed;

    public DriverAapTransport(string interfacePath, ILog log) { _path = interfacePath; _log = log; }

    public string Name => "MagicAAP L2CAP 0x1001";
    public bool IsOpen => _stream is not null && Volatile.Read(ref _closed) == 0;
    public event Action<byte[]>? PacketReceived;
    public event Action<Exception?>? Closed;

    public Task OpenAsync(CancellationToken ct)
    {
        var h = Native.CreateFileW(_path, Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            throw new IOException($"Cannot open AAP driver interface: {new Win32Exception(err).Message} (0x{err:X})");
        }
        _stream = new FileStream(h, FileAccess.ReadWrite, bufferSize: 0, isAsync: true);
        _closed = 0;
        _readCts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoop(_readCts.Token));
        _log.Info("Opened AAP driver interface");
        return Task.CompletedTask;
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        var buf = new byte[4096];
        Exception? error = null;
        try
        {
            while (!ct.IsCancellationRequested && _stream is { } s)
            {
                int n = await s.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
                if (n <= 0) break;
                PacketReceived?.Invoke(buf.AsSpan(0, n).ToArray());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { error = ex; }
        RaiseClosed(error);
    }

    public async Task SendAsync(byte[] packet, CancellationToken ct)
    {
        var s = _stream ?? throw new IOException("Control channel is not open.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await s.WriteAsync(packet, timeout.Token).ConfigureAwait(false);
    }

    private void RaiseClosed(Exception? ex)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        if (ex is not null) _log.Warn("AAP channel closed with error", ex); else _log.Info("AAP channel closed");
        Closed?.Invoke(ex);
    }

    public ValueTask DisposeAsync()
    {
        _readCts?.Cancel();
        try { _stream?.Dispose(); } catch { }
        _stream = null;
        RaiseClosed(null);
        return ValueTask.CompletedTask;
    }
}
