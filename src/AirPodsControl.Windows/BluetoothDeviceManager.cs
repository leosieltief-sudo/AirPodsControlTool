using System.Text.RegularExpressions;
using AirPodsControl.Core.Models;
using AirPodsControl.Core.Protocol;
using AirPodsControl.Core.Services;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace AirPodsControl.Windows;

/// <summary>A paired Apple audio device found in the BTHENUM device tree.</summary>
public sealed record PairedAppleDevice(ulong Address, ushort ProductId, string? AapNodeInstanceId, string? AapNodeDriver)
{
    public string Mac => string.Join(":", Enumerable.Range(0, 6).Select(i => ((Address >> (40 - 8 * i)) & 0xFF).ToString("X2")));
    public string MacHex => Address.ToString("X12");
    public AirPodsModelInfo Model => AirPodsCatalog.ByProductId(ProductId) ?? AirPodsCatalog.Unknown(ProductId);
}

public enum RadioStatus { Unknown, NoAdapter, Off, On }

/// <summary>
/// Finds paired AirPods and tracks their classic-Bluetooth connection state.
/// Identification uses the Device ID profile values Windows puts into the BTHENUM hardware id:
/// "_VID&0001004C_PID&xxxx" (0x004C = Apple, source 0001 = Bluetooth SIG). No name guessing.
/// </summary>
public sealed partial class BluetoothDeviceManager : IDisposable
{
    private readonly ILog _log;
    private DeviceWatcher? _watcher;
    private Radio? _radio;
    private BluetoothDevice? _tracked;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BluetoothDeviceManager(ILog log) => _log = log;

    public RadioStatus Radio { get; private set; } = RadioStatus.Unknown;
    public event Action? RadioChanged;
    /// <summary>Pairing list changed (device paired / removed).</summary>
    public event Action? PairedDevicesChanged;
    public event Action<bool>? TrackedConnectionChanged;

    [GeneratedRegex(@"^BTHENUM\\\{(?<svc>[0-9A-F\-]{36})\}_VID&0001004C_PID&(?<pid>[0-9A-F]{4})\\.*&(?<mac>[0-9A-F]{12})_C[0-9A-F]+$", RegexOptions.IgnoreCase)]
    private static partial Regex AppleServiceNode();

    /// <summary>Enumerates paired Apple devices with an "AAP Server" service node. Pure device-tree query, no radio traffic.</summary>
    public static IReadOnlyList<PairedAppleDevice> FindPairedAppleAudioDevices()
    {
        var byMac = new Dictionary<ulong, (ushort pid, string? aapNode)>();
        foreach (var id in Native.GetPresentDeviceIds("BTHENUM"))
        {
            var m = AppleServiceNode().Match(id);
            if (!m.Success) continue;
            ulong addr = Convert.ToUInt64(m.Groups["mac"].Value, 16);
            ushort pid = Convert.ToUInt16(m.Groups["pid"].Value, 16);
            bool isAap = Guid.TryParse(m.Groups["svc"].Value, out var svc) && svc == AapPackets.AapServiceUuid;
            byMac.TryGetValue(addr, out var cur);
            byMac[addr] = (pid, isAap ? id : cur.aapNode);
        }
        return byMac
            // Only devices that expose the AAP service are AirPods/Beats-with-AAP; other Apple devices (Magic Keyboard...) are skipped.
            .Where(kv => kv.Value.aapNode is not null || AirPodsCatalog.ByProductId(kv.Value.pid) is not null)
            .Select(kv => new PairedAppleDevice(kv.Key, kv.Value.pid, kv.Value.aapNode,
                kv.Value.aapNode is null ? null : Native.GetBoundService(kv.Value.aapNode)))
            .ToList();
    }

    public async Task StartAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null) { SetRadio(RadioStatus.NoAdapter); }
            else
            {
                _radio = await adapter.GetRadioAsync();
                if (_radio is null) SetRadio(RadioStatus.Unknown);
                else
                {
                    _radio.StateChanged += (r, _) => SetRadio(r.State == RadioState.On ? RadioStatus.On : RadioStatus.Off);
                    SetRadio(_radio.State == RadioState.On ? RadioStatus.On : RadioStatus.Off);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Could not query Bluetooth radio", ex);
            SetRadio(RadioStatus.Unknown);
        }

        try
        {
            _watcher = DeviceInformation.CreateWatcher(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
            _watcher.Added += (_, _) => { if (_watcher.Status == DeviceWatcherStatus.EnumerationCompleted) PairedDevicesChanged?.Invoke(); };
            _watcher.Removed += (_, _) => PairedDevicesChanged?.Invoke();
            _watcher.Updated += (_, _) => { };
            _watcher.EnumerationCompleted += (_, _) => PairedDevicesChanged?.Invoke();
            _watcher.Start();
        }
        catch (Exception ex) { _log.Warn("DeviceWatcher failed", ex); }
    }

    private void SetRadio(RadioStatus s)
    {
        if (s == Radio) return;
        Radio = s;
        _log.Info($"Bluetooth radio: {s}");
        RadioChanged?.Invoke();
    }

    /// <summary>Starts tracking one device's connection state. Returns (name, connected).</summary>
    public async Task<(string? Name, bool Connected)> TrackAsync(ulong address)
    {
        await _gate.WaitAsync();
        try
        {
            if (_tracked is not null && _tracked.BluetoothAddress == address)
                return (_tracked.Name, _tracked.ConnectionStatus == BluetoothConnectionStatus.Connected);
            Untrack();
            var dev = await BluetoothDevice.FromBluetoothAddressAsync(address);
            if (dev is null) return (null, false);
            _tracked = dev;
            dev.ConnectionStatusChanged += OnConnChanged;
            return (dev.Name, dev.ConnectionStatus == BluetoothConnectionStatus.Connected);
        }
        catch (Exception ex)
        {
            _log.Warn("BluetoothDevice.FromBluetoothAddressAsync failed", ex);
            return (null, false);
        }
        finally { _gate.Release(); }
    }

    public bool? TrackedConnected => _tracked is null ? null : _tracked.ConnectionStatus == BluetoothConnectionStatus.Connected;

    private void OnConnChanged(BluetoothDevice d, object _)
    {
        bool c = d.ConnectionStatus == BluetoothConnectionStatus.Connected;
        _log.Info($"{d.Name}: {(c ? "connected" : "disconnected")}");
        TrackedConnectionChanged?.Invoke(c);
    }

    private void Untrack()
    {
        if (_tracked is null) return;
        _tracked.ConnectionStatusChanged -= OnConnChanged;
        _tracked.Dispose();
        _tracked = null;
    }

    public void Dispose()
    {
        try { if (_watcher is { Status: DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted }) _watcher.Stop(); } catch { }
        Untrack();
    }
}
