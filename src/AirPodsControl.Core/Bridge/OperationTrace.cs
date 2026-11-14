using System.Diagnostics;
using System.Globalization;

namespace AirPodsControl.Core.Bridge;

public enum BridgeOperationKind { SwitchMode, ReadBattery, AdapterTest, AirPodsTest, Recovery, HoldCycle }

/// <summary>Who produced a trace event: an action the app performed, or something it only observed.</summary>
public enum TraceOrigin
{
    /// <summary>Action taken by this app (radio off, attach, request to the daemon, detach…).</summary>
    App,
    /// <summary>Observed on Windows (AirPods connection status, radio state, audio endpoint).</summary>
    Windows,
    /// <summary>Observed by the Linux kernel's management interface (ACL connect/disconnect, adapter index).</summary>
    Linux,
    /// <summary>AAP packets on the L2CAP channel.</summary>
    Aap,
    /// <summary>Daemon stage marks (its own actions inside WSL).</summary>
    Daemon,
}

public sealed record TraceEvent(double At, TraceOrigin Origin, string Name, string? Detail);

/// <summary>
/// One bridge operation (one adapter hand-over at most): id, initiator and time-stamped events from the app, Windows,
/// the daemon and the Linux kernel. Events keep arriving for a while after the operation ended (reconnection to Windows).
/// </summary>
public sealed class OperationTrace
{
    private static int _seq;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly List<TraceEvent> _events = [];
    private readonly Action<OperationTrace, TraceEvent>? _sink;

    public OperationTrace(BridgeOperationKind kind, string initiator, Action<OperationTrace, TraceEvent>? sink = null)
    {
        Kind = kind;
        Initiator = string.IsNullOrWhiteSpace(initiator) ? "unknown" : initiator;
        Id = "op" + Interlocked.Increment(ref _seq).ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..4];
        StartedAt = DateTimeOffset.Now;
        _sink = sink;
    }

    public string Id { get; }
    public BridgeOperationKind Kind { get; }
    public string Initiator { get; }
    public DateTimeOffset StartedAt { get; }
    /// <summary>Seconds since the operation started.</summary>
    public double Now => _sw.Elapsed.TotalSeconds;
    public string Header => $"{Id} {Kind} by {Initiator}";

    public void App(string name, string? detail = null) => Add(TraceOrigin.App, name, detail, null);

    /// <param name="at">Seconds since the operation start when the event happened (default: now).</param>
    public void Observed(TraceOrigin origin, string name, string? detail = null, double? at = null) => Add(origin, name, detail, at);

    private void Add(TraceOrigin origin, string name, string? detail, double? at)
    {
        var e = new TraceEvent(at ?? Now, origin, name, detail);
        lock (_events) _events.Add(e);
        try { _sink?.Invoke(this, e); } catch { /* tracing must never break the operation */ }
    }

    public IReadOnlyList<TraceEvent> Events { get { lock (_events) return _events.OrderBy(e => e.At).ToList(); } }

    public int Count(TraceOrigin origin, string name) { lock (_events) return _events.Count(e => e.Origin == origin && e.Name == name); }

    public static string Format(OperationTrace op, TraceEvent e) =>
        string.Create(CultureInfo.InvariantCulture, $"[{op.Id} {op.Initiator}] +{e.At,7:0.000} {e.Origin,-7} {e.Name}{(e.Detail is null ? "" : " " + e.Detail)}");

    /// <summary>Counts that answer "how many times did the AirPods lose a link" for this operation.</summary>
    public string Summary() =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Header}: attach={Count(TraceOrigin.App, "attach_ok")} detach={Count(TraceOrigin.App, "detach")} " +
            $"windows_airpods_disconnected={Count(TraceOrigin.Windows, "airpods_disconnected")} windows_airpods_connected={Count(TraceOrigin.Windows, "airpods_connected")} " +
            $"audio_endpoint_off={Count(TraceOrigin.Windows, "audio_endpoint_disabled")} audio_endpoint_on={Count(TraceOrigin.Windows, "audio_endpoint_enabled")} " +
            $"linux_acl_connected={Count(TraceOrigin.Linux, "acl_connected")} linux_acl_disconnected={Count(TraceOrigin.Linux, "acl_disconnected")} " +
            $"linux_connect_failed={Count(TraceOrigin.Linux, "connect_failed") + Count(TraceOrigin.Daemon, "connect_failed")}");
}
