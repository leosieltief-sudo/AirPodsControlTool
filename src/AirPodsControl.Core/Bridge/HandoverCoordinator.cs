using AirPodsControl.Core.Protocol;
using AirPodsControl.Core.Services;

namespace AirPodsControl.Core.Bridge;

/// <summary>Platform side of an adapter hand-over (Windows: usbipd, WinRT radio, device tree, lock file).</summary>
public interface IHandoverHost
{
    /// <summary>Cross-process lock (app and CLI must never hand the adapter over at the same time). Null = held by someone else.</summary>
    IDisposable? TryAcquireSystemLock(OperationTrace op, out string? holder);
    Task<AdapterResolution> ResolveAdapterAsync(OperationTrace op, CancellationToken ct);
    Task<InputGuardReport> CheckInputDevicesAsync(AdapterTarget target, ulong? excludeAddress, OperationTrace op, CancellationToken ct);
    /// <summary>Asks the user (keyboard-accessible dialog). Hosts without a user (CLI) return false.</summary>
    Task<bool> ConfirmInputLossAsync(InputGuardReport report, OperationTrace op);
    /// <summary>Switches off only <paramref name="target"/>'s radio. Returns whether it was switched off by us.</summary>
    Task<(bool turnedOff, string? error)> RadioOffAsync(AdapterTarget target, OperationTrace op);
    Task<string?> AttachAsync(AdapterTarget target, OperationTrace op, CancellationToken ct);
    Task DetachAsync(AdapterTarget target, OperationTrace op);
    Task RadioOnAsync(AdapterTarget target, OperationTrace op);
    void OperationStarted(OperationTrace op, ulong? device);
    void OperationFinished(OperationTrace op);
}

/// <param name="InputLossApproved">An explicit, input-specific approval given before the operation (CLI
/// --allow-input-loss). A general "--yes" must not set it.</param>
public sealed record HandoverRequest(BridgeOperationKind Kind, string Initiator, ulong? Device = null, bool InputLossApproved = false);

public enum HandoverStage { Busy, Lock, Adapter, InputGuard, Prepare, Radio, Attach, Operation, Done }

/// <param name="Disrupted">Windows lost the radio and/or the adapter during this operation (its Bluetooth links dropped).</param>
public sealed record HandoverResult<T>(T? Value, string? Error, HandoverStage Stage, OperationTrace Op, bool Disrupted)
{
    public bool Completed => Stage == HandoverStage.Done;
}

public enum MergeResult
{
    /// <summary>Will be sent in the running hand-over (latest wins).</summary>
    Merged,
    /// <summary>Same as what is already being applied: nothing to do.</summary>
    DuplicateIgnored,
    /// <summary>No hand-over is running, or the adapter is already on its way back: ask again later.</summary>
    NotAccepting,
}

/// <summary>The running hand-over as seen by the operation (adapter is in WSL).</summary>
public sealed class HandoverSession
{
    private readonly HandoverCoordinator _owner;
    internal HandoverSession(HandoverCoordinator owner, OperationTrace op, AdapterTarget target) { _owner = owner; Op = op; Target = target; }
    public OperationTrace Op { get; }
    public AdapterTarget Target { get; }
    /// <summary>Announces the mode currently being sent (repeat presses of it are ignored).</summary>
    public void Sending(ListeningMode mode) => _owner.SetInFlight(mode);
    /// <summary>The newest request merged meanwhile, or null — in which case the session stops accepting merges atomically.</summary>
    public ModeSwitchRequest? NextOrClose() => _owner.NextOrClose();
}

/// <summary>
/// Runs every adapter hand-over: exactly one at a time (in this process, and across processes via the host lock),
/// all checks before anything is switched off, and the adapter + radio always returned in <c>finally</c>
/// (errors and cancellation included). Requests made during a running mode switch are merged into it while the adapter
/// is still in WSL (latest wins); nothing is ever retried automatically.
/// </summary>
public sealed class HandoverCoordinator(IHandoverHost host, Action<OperationTrace, TraceEvent>? sink = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private OperationTrace? _current;
    private bool _accepting;
    private ModeSwitchRequest? _merged;
    private ListeningMode? _inFlight;

    public bool IsBusy { get { lock (_lock) return _current is not null; } }
    public OperationTrace? Current { get { lock (_lock) return _current; } }
    public OperationTrace? Last { get; private set; }

    public OperationTrace NewTrace(BridgeOperationKind kind, string initiator) => new(kind, initiator, sink);

    public MergeResult TryMerge(ModeSwitchRequest request)
    {
        lock (_lock)
        {
            if (_current is null || !_accepting) return MergeResult.NotAccepting;
            var wanted = _merged?.Mode ?? _inFlight;
            if (request.Mode == wanted) return MergeResult.DuplicateIgnored;
            if (request.Mode == _inFlight)
            {
                _merged = null;   // back to what is being sent anyway: drop the pending change
                _current.App("merge_cancelled", $"mode={request.Mode} initiator={request.Initiator}");
                return MergeResult.Merged;
            }
            _merged = request;
            _current.App("merge", $"mode={request.Mode} initiator={request.Initiator}");
            return MergeResult.Merged;
        }
    }

    internal void SetInFlight(ListeningMode mode) { lock (_lock) _inFlight = mode; }

    internal ModeSwitchRequest? NextOrClose()
    {
        lock (_lock)
        {
            var next = _merged;
            _merged = null;
            if (next is null) _accepting = false;
            else _inFlight = next.Mode;
            return next;
        }
    }

    /// <param name="prepare">Non-disruptive preparation (start WSL/daemon, update scripts); runs after all checks, before the radio is touched.</param>
    /// <param name="operation">Runs while the adapter is attached to WSL.</param>
    /// <param name="beforeDetach">Graceful shutdown inside WSL (close AAP channel, drop ACL) before usbipd detach.</param>
    public async Task<HandoverResult<T>> RunAsync<T>(HandoverRequest request, Func<OperationTrace, CancellationToken, Task<string?>>? prepare,
        Func<HandoverSession, CancellationToken, Task<T>> operation, Func<HandoverSession, Task>? beforeDetach, CancellationToken ct,
        OperationTrace? trace = null)
    {
        var op = trace ?? NewTrace(request.Kind, request.Initiator);
        op.App("begin", $"kind={request.Kind} initiator={request.Initiator}");
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            var busy = Current;
            op.App("rejected_busy", busy?.Header);
            return new(default, "Another WSL bridge operation is running" + (busy is null ? "." : $" ({busy.Kind} started by {busy.Initiator}, {busy.Now:0}s ago)."), HandoverStage.Busy, op, false);
        }
        IDisposable? sysLock = null;
        AdapterTarget? target = null;
        bool turnedOff = false, attached = false;
        HandoverSession? session = null;
        try
        {
            sysLock = host.TryAcquireSystemLock(op, out var holder);
            if (sysLock is null)
            {
                op.App("rejected_locked", holder);
                return new(default, "Another AirPods Control process is using the Bluetooth adapter" + (holder is null ? "." : $": {holder}."), HandoverStage.Lock, op, false);
            }
            lock (_lock) { _current = op; _accepting = false; _merged = null; _inFlight = null; }
            host.OperationStarted(op, request.Device);

            // ---- checks: nothing has been switched off yet
            var res = await host.ResolveAdapterAsync(op, ct).ConfigureAwait(false);
            op.App("adapter_resolved", res.Ok ? res.Target!.Description : "error: " + res.Error);
            if (!res.Ok) return new(default, res.Error, HandoverStage.Adapter, op, false);
            target = res.Target!;

            var report = await host.CheckInputDevicesAsync(target, request.Device, op, ct).ConfigureAwait(false);
            op.App("input_guard", $"{report.Status}: {report.Explain()}");
            if (report.Status != InputGuardStatus.Clear)
            {
                bool approved = request.InputLossApproved || await host.ConfirmInputLossAsync(report, op).ConfigureAwait(false);
                op.App(approved ? "input_guard_overridden" : "input_guard_blocked", request.InputLossApproved ? "pre-approved" : "dialog");
                if (!InputGuardPolicy.MayProceed(report, approved))
                    return new(default, report.Explain() + " Stopped before turning Bluetooth off.", HandoverStage.InputGuard, op, false);
            }

            if (prepare is not null)
            {
                var perr = await prepare(op, ct).ConfigureAwait(false);
                if (perr is not null) { op.App("prepare_failed", perr); return new(default, perr, HandoverStage.Prepare, op, false); }
                op.App("prepared");
            }

            // ---- disruptive part
            ct.ThrowIfCancellationRequested();
            var (off, rerr) = await host.RadioOffAsync(target, op).ConfigureAwait(false);
            turnedOff = off;
            if (rerr is not null) return new(default, rerr, HandoverStage.Radio, op, turnedOff);
            var aerr = await host.AttachAsync(target, op, ct).ConfigureAwait(false);
            if (aerr is not null) return new(default, aerr, HandoverStage.Attach, op, turnedOff);
            attached = true;
            session = new HandoverSession(this, op, target);
            lock (_lock) _accepting = request.Kind == BridgeOperationKind.SwitchMode;
            var value = await operation(session, ct).ConfigureAwait(false);
            return new(value, null, HandoverStage.Done, op, true);
        }
        catch (OperationCanceledException)
        {
            op.App("cancelled");
            throw;
        }
        catch (Exception ex)
        {
            op.App("error", ex.GetType().Name + ": " + ex.Message);
            return new(default, "WSL bridge error: " + ex.Message, HandoverStage.Operation, op, attached || turnedOff);
        }
        finally
        {
            lock (_lock) { _accepting = false; _merged = null; }
            if (attached && session is not null && beforeDetach is not null)
            {
                try { await beforeDetach(session).ConfigureAwait(false); }
                catch (Exception ex) { op.App("release_error", ex.Message); }
            }
            if (attached && target is not null)
            {
                try { await host.DetachAsync(target, op).ConfigureAwait(false); }
                catch (Exception ex) { op.App("detach_error", ex.Message); }
            }
            if (turnedOff && target is not null)
            {
                try { await host.RadioOnAsync(target, op).ConfigureAwait(false); }
                catch (Exception ex) { op.App("radio_on_error", ex.Message); }
            }
            op.App("end");
            Last = op;
            lock (_lock) { _current = null; _inFlight = null; }
            if (sysLock is not null)
            {
                try { host.OperationFinished(op); } catch { }
                sysLock.Dispose();
            }
            _gate.Release();
        }
    }

    /// <summary>Exclusive, non-hand-over work (startup recovery). Returns false when an operation is running.</summary>
    public async Task<bool> TryRunExclusiveAsync(BridgeOperationKind kind, string initiator, Func<OperationTrace, Task> work)
    {
        var op = NewTrace(kind, initiator);
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) { op.App("skipped_busy"); return false; }
        try
        {
            using var sysLock = host.TryAcquireSystemLock(op, out var holder);
            if (sysLock is null) { op.App("skipped_locked", holder); return false; }
            lock (_lock) _current = op;
            try { await work(op).ConfigureAwait(false); }
            finally { lock (_lock) _current = null; Last = op; }
            return true;
        }
        finally { _gate.Release(); }
    }
}
