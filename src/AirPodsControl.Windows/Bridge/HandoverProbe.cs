using AirPodsControl.Core.Bridge;
using AirPodsControl.Core.Services;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace AirPodsControl.Windows.Bridge;

/// <summary>
/// Observes Windows during a hand-over and for a while after it (reconnection): the AirPods' classic connection state,
/// Windows' Bluetooth adapter/radio, and the AirPods audio endpoint (what the user actually hears drop). Everything is
/// recorded as <see cref="TraceOrigin.Windows"/> events — observations, not actions — so the trace shows which link
/// drops the app causes and which ones follow from the controller re-initialising.
/// </summary>
public sealed class HandoverProbe : IDisposable
{
    private readonly OperationTrace _op;
    private readonly ulong? _device;
    private readonly ILog _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _tail;
    private DeviceWatcher? _audio;
    private readonly Dictionary<string, string> _endpoints = new();
    private bool _audioEnumerated;
    private double? _finishedAt;
    private Task? _loop;

    public HandoverProbe(OperationTrace op, ulong? device, ILog log, TimeSpan? tail = null)
    {
        _op = op; _device = device; _log = log; _tail = tail ?? TimeSpan.FromSeconds(20);
    }

    public void Start()
    {
        StartAudioWatcher();
        _loop = Task.Run(() => PollAsync(_cts.Token));
    }

    /// <summary>The operation ended; keep observing the reconnection for the tail period.</summary>
    public void Finish() => _finishedAt = _op.Now;

    private async Task PollAsync(CancellationToken ct)
    {
        string? lastLink = null, lastRadio = null;
        double connectedSince = -1;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var link = await LinkStateAsync().ConfigureAwait(false);
                if (link != lastLink)
                {
                    _op.Observed(TraceOrigin.Windows, lastLink is null ? "airpods_initial" : "airpods_" + link, lastLink is null ? link : $"was {lastLink}");
                    lastLink = link;
                    connectedSince = link == "connected" ? _op.Now : -1;
                }
                var radio = await RadioStateAsync().ConfigureAwait(false);
                if (radio != lastRadio)
                {
                    _op.Observed(TraceOrigin.Windows, "radio_state", radio);
                    lastRadio = radio;
                }
                if (_finishedAt is { } f)
                {
                    bool settled = connectedSince >= 0 && _op.Now - connectedSince > 3 && _op.Now - f > 3;
                    if (settled || _op.Now - f > _tail.TotalSeconds) break;
                }
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Warn("hand-over probe stopped: " + ex.Message); }
        finally
        {
            StopAudioWatcher();
            _op.Observed(TraceOrigin.Windows, "probe_end");
            _log.Info("bridge trace summary: " + _op.Summary());
        }
    }

    private async Task<string> LinkStateAsync()
    {
        if (_device is not { } addr) return "n/a";
        try
        {
            using var d = await BluetoothDevice.FromBluetoothAddressAsync(addr);
            return d is null ? "unavailable" : d.ConnectionStatus == BluetoothConnectionStatus.Connected ? "connected" : "disconnected";
        }
        catch { return "unavailable"; }
    }

    private static async Task<string> RadioStateAsync()
    {
        try
        {
            var a = await BluetoothAdapter.GetDefaultAsync();
            if (a is null) return "no_adapter";
            var r = await a.GetRadioAsync();
            return r is null ? "adapter_without_radio" : r.State.ToString();
        }
        catch { return "unknown"; }
    }

    private void StartAudioWatcher()
    {
        try
        {
            var w = DeviceInformation.CreateWatcher(MediaDevice.GetAudioRenderSelector());
            w.Added += (_, di) =>
            {
                if (!IsAirPods(di.Name)) return;
                lock (_endpoints) _endpoints[di.Id] = di.Name;
                _op.Observed(TraceOrigin.Windows, _audioEnumerated ? "audio_endpoint_enabled" : "audio_endpoint_initial", di.Name);
            };
            w.Removed += (_, u) =>
            {
                string? name;
                lock (_endpoints) _endpoints.TryGetValue(u.Id, out name);
                if (name is not null) _op.Observed(TraceOrigin.Windows, "audio_endpoint_disabled", name);
            };
            w.Updated += (_, u) =>
            {
                string? name;
                lock (_endpoints) _endpoints.TryGetValue(u.Id, out name);
                if (name is not null && u.Properties.TryGetValue("System.Devices.InterfaceEnabled", out var v) && v is bool en)
                    _op.Observed(TraceOrigin.Windows, en ? "audio_endpoint_enabled" : "audio_endpoint_disabled", name);
            };
            w.EnumerationCompleted += (_, _) => _audioEnumerated = true;
            w.Start();
            _audio = w;
        }
        catch (Exception ex) { _op.Observed(TraceOrigin.Windows, "audio_watch_unavailable", ex.Message); }
    }

    private static bool IsAirPods(string? name) => name is not null && (name.Contains("AirPods", StringComparison.OrdinalIgnoreCase) || name.Contains("Beats", StringComparison.OrdinalIgnoreCase));

    private void StopAudioWatcher()
    {
        try { if (_audio is { Status: DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted }) _audio.Stop(); } catch { }
        _audio = null;
    }

    /// <summary>Stops at once (a new operation starts).</summary>
    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }

    /// <summary>Waits until the tail is over (CLI: print the full trace before exiting).</summary>
    public Task Completion => _loop ?? Task.CompletedTask;
}
