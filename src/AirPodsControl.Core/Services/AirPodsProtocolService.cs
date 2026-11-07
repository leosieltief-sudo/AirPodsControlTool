using System.Collections.Concurrent;
using AirPodsControl.Core.Protocol;

namespace AirPodsControl.Core.Services;

/// <summary>
/// Owns one AAP session over a transport: performs the handshake, dispatches parsed notifications,
/// and lets callers send a control command and await the device's echo (= the actual confirmation).
/// </summary>
public sealed class AirPodsProtocolService : IAsyncDisposable
{
    private readonly IAapTransport _transport;
    private readonly ILog _log;
    private readonly ConcurrentDictionary<byte, TaskCompletionSource<ControlCommandMessage>> _waiters = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public AirPodsProtocolService(IAapTransport transport, ILog? log = null)
    {
        _transport = transport;
        _log = log ?? NullLog.Instance;
        _transport.PacketReceived += OnPacket;
        _transport.Closed += OnClosed;
    }

    public IAapTransport Transport => _transport;
    public bool IsOpen => _transport.IsOpen;
    public bool HandshakeDone { get; private set; }

    /// <summary>Last value seen for each control identifier (from notifications or echoes).</summary>
    public ConcurrentDictionary<byte, ControlCommandMessage> ControlState { get; } = new();

    public event Action<BatteryMessage>? BatteryReceived;
    public event Action<EarDetectionMessage>? EarDetectionReceived;
    public event Action<ControlCommandMessage>? ControlReceived;
    public event Action<MetadataMessage>? MetadataReceived;
    public event Action<Exception?>? Closed;

    /// <summary>Delay between init packets; LibrePods / AirPodsWindows both pace the init sequence.</summary>
    public TimeSpan InitPacketGap { get; init; } = TimeSpan.FromMilliseconds(100);

    public async Task StartAsync(CancellationToken ct)
    {
        if (!_transport.IsOpen) await _transport.OpenAsync(ct).ConfigureAwait(false);
        await SendRawAsync(AapPackets.Handshake(), ct).ConfigureAwait(false);
        await Task.Delay(InitPacketGap, ct).ConfigureAwait(false);
        await SendRawAsync(AapPackets.SetFeatureFlags(), ct).ConfigureAwait(false);
        await Task.Delay(InitPacketGap, ct).ConfigureAwait(false);
        await SendRawAsync(AapPackets.RequestNotifications(), ct).ConfigureAwait(false);
        HandshakeDone = true;
        _log.Info($"AAP session initialised over {_transport.Name}");
    }

    public async Task SendRawAsync(byte[] packet, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _log.Debug($"TX {AapPackets.ToSpacedHex(packet)}");
            await _transport.SendAsync(packet, ct).ConfigureAwait(false);
        }
        finally { _sendGate.Release(); }
    }

    /// <summary>
    /// Sends a control command and waits for the next control packet with the same identifier coming back
    /// from the AirPods. Returns null on timeout. A successful send alone is NOT treated as success.
    /// </summary>
    public async Task<ControlCommandMessage?> SendControlAndAwaitAsync(byte identifier, byte[] packet, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ControlCommandMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters[identifier] = tcs;
        try
        {
            await SendRawAsync(packet, ct).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(timeout, cts.Token);
            var done = await Task.WhenAny(tcs.Task, delay).ConfigureAwait(false);
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return done == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            _waiters.TryRemove(new KeyValuePair<byte, TaskCompletionSource<ControlCommandMessage>>(identifier, tcs));
        }
    }

    private void OnPacket(byte[] data)
    {
        _log.Debug($"RX {AapPackets.ToSpacedHex(data)}");
        AapMessage msg;
        try { msg = AapParser.Parse(data); }
        catch (Exception ex) { _log.Warn("Parser failure", ex); return; }

        try
        {
            switch (msg)
            {
                case BatteryMessage b: BatteryReceived?.Invoke(b); break;
                case EarDetectionMessage e: EarDetectionReceived?.Invoke(e); break;
                case MetadataMessage m: MetadataReceived?.Invoke(m); break;
                case ControlCommandMessage c:
                    ControlState[c.Identifier] = c;
                    if (_waiters.TryRemove(c.Identifier, out var w)) w.TrySetResult(c);
                    ControlReceived?.Invoke(c);
                    break;
            }
        }
        catch (Exception ex) { _log.Warn("Notification handler failed", ex); }
    }

    private void OnClosed(Exception? ex)
    {
        HandshakeDone = false;
        foreach (var w in _waiters.Values) w.TrySetCanceled();
        _waiters.Clear();
        Closed?.Invoke(ex);
    }

    public async ValueTask DisposeAsync()
    {
        _transport.PacketReceived -= OnPacket;
        _transport.Closed -= OnClosed;
        await _transport.DisposeAsync().ConfigureAwait(false);
        _sendGate.Dispose();
    }
}
