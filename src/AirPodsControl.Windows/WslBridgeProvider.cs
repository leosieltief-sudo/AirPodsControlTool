using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AirPodsControl.Core.Bridge;
using AirPodsControl.Core.Protocol;
using AirPodsControl.Core.Services;
using AirPodsControl.Windows.Bridge;
using Windows.Devices.Radios;

namespace AirPodsControl.Windows;

/// <summary>
/// Driverless backend: hands the USB Bluetooth adapter to a small WSL2 distribution with usbipd-win for the duration
/// of one operation and always returns it to Windows. Windows Bluetooth (audio, mouse, keyboard) is unavailable while the
/// adapter is in Linux — inherent to sharing one radio between two hosts.
/// Every hand-over goes through <see cref="HandoverCoordinator"/>: one at a time (also across processes), adapter mapping
/// and input-device protection checked before anything is switched off, a time-stamped <see cref="OperationTrace"/>.
/// Mode switches and exact battery reads use the persistent daemon (aapd.py); the per-command bridge.sh path remains
/// for the wizard's tests and as a fallback.
/// </summary>
public sealed class WslBridgeProvider : IModeSwitchProvider, IHandoverHost
{
    private readonly ILog _log;
    private readonly UsbipdClient _usbipd;
    private readonly WslClient _wsl;
    private readonly BluetoothRadioSwitch _radio;
    private BridgeDaemonClient _daemon;
    private HandoverProbe? _probe;
    private static readonly object TraceFileLock = new();

    public WslBridgeProvider(ILog log)
    {
        _log = log;
        _usbipd = new UsbipdClient(log);
        _wsl = new WslClient(log);
        _radio = new BluetoothRadioSwitch(log);
        Config = BridgeConfig.Load();
        _daemon = new BridgeDaemonClient(log, Config.Distro);
        Coordinator = new HandoverCoordinator(this, WriteTrace);
    }

    public HandoverCoordinator Coordinator { get; }
    public BridgeConfig Config { get; private set; }
    public ControlBackend Kind => ControlBackend.WslBridge;
    public string DisplayName => "WSL2 bridge";
    public bool InterruptsAudio => true;
    public bool IsBusy => Coordinator.IsBusy;
    /// <summary>Full result of the last bridge session (mode, exact battery) for the controller/diagnostics.</summary>
    public BridgeResult? LastResult { get; private set; }
    /// <summary>Address of the AirPods currently selected in the app (keys are per device).</summary>
    public ulong? CurrentDevice { get; set; }
    /// <summary>Trace of the last operation (events keep arriving during the reconnection tail).</summary>
    public OperationTrace? LastTrace { get; private set; }
    /// <summary>Whether the last operation made Windows lose the radio/adapter (false: stopped before anything happened).</summary>
    public bool LastOperationDisrupted { get; private set; }
    /// <summary>Windows-side observer of the last operation (CLI waits for it to print the full trace).</summary>
    public HandoverProbe? LastProbe => _probe;
    /// <summary>Diagnostics: log every AAP packet (PKT lines in <see cref="LastOutput"/>).</summary>
    public bool Trace { get; set; }
    /// <summary>Raw output of the last bridge run (RESULT/PKT/STAGE lines) for diagnostics.</summary>
    public string? LastOutput { get; private set; }
    /// <summary>Diagnostics only: extra bridge.sh run flags (--own, --features XX, --settle S, --a2dp). Forces the bridge.sh path.</summary>
    public List<string> ExtraArgs { get; } = [];
    /// <summary>
    /// Asked when a Bluetooth mouse/keyboard would lose its connection (or ownership is unclear). Must be an explicit,
    /// keyboard-accessible user decision; null (CLI without --allow-input-loss) means "no".
    /// </summary>
    public Func<InputGuardReport, OperationTrace, Task<bool>>? ConfirmInputLoss { get; set; }

    public static string TraceFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AirPodsControl", "logs", $"bridge-trace-{DateTime.Now:yyyyMMdd}.log");

    public void ReloadConfig()
    {
        var distro = Config.Distro;
        Config = BridgeConfig.Load();
        if (!string.Equals(distro, Config.Distro, StringComparison.Ordinal)) { _daemon.Dispose(); _daemon = new BridgeDaemonClient(_log, Config.Distro); }
    }

    /// <summary>Stops the daemon (scripts were replaced, distribution is being removed, WSL restarts).</summary>
    public void StopDaemon() => _daemon.Stop();

    public ProviderStatus Status
    {
        get
        {
            var c = Config;
            if (!c.Provisioned || c.ScriptVersion < BridgeConfig.MinReadyScriptVersion) return ProviderStatus.NotReady("not set up (open Driverless setup)");
            if (c.AdapterVidPid is null || !_usbipd.IsInstalled) return ProviderStatus.NotReady("usbipd-win or adapter sharing missing (run setup)");
            if (CurrentDevice is { } d && !c.HasKeyFor(d)) return ProviderStatus.NotReady("pairing key for these AirPods not copied yet (run setup)");
            if (c.AdapterTestOk != true) return ProviderStatus.NotReady(c.AdapterTestOk == false
                ? "adapter test failed: " + (c.AdapterTestMessage ?? "?") : "adapter not tested yet (run setup → Test)");
            return new ProviderStatus(true, "ready (Bluetooth pauses while switching)");
        }
    }

    public static string ModeArg(ListeningMode m) => m switch
    {
        ListeningMode.Off => "off",
        ListeningMode.NoiseCancellation => "anc",
        ListeningMode.Transparency => "transparency",
        ListeningMode.Adaptive => "adaptive",
        _ => throw new ArgumentOutOfRangeException(nameof(m)),
    };

    private static string Inv(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void WriteTrace(OperationTrace op, TraceEvent e)
    {
        var line = OperationTrace.Format(op, e);
        _log.Debug("trace " + line);
        try
        {
            lock (TraceFileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TraceFile)!);
                File.AppendAllText(TraceFile, DateTime.Now.ToString("HH:mm:ss.fff ") + line + Environment.NewLine);
            }
        }
        catch { }
    }

    private void Finish<T>(HandoverResult<T> r)
    {
        LastTrace = r.Op;
        LastOperationDisrupted = r.Disrupted;
        if (!r.Completed) _log.Info($"bridge {r.Op.Header}: stopped at {r.Stage}: {r.Error}");
    }

    private static NoiseControlResult NotCompleted<T>(HandoverResult<T> r, ListeningMode mode) => r.Stage switch
    {
        HandoverStage.Busy or HandoverStage.Lock or HandoverStage.Adapter or HandoverStage.InputGuard or HandoverStage.Prepare =>
            new(NoiseControlOutcome.NotStarted, mode, null, r.Error ?? "Not started."),
        _ => new(NoiseControlOutcome.TransportError, mode, null, r.Error ?? "WSL bridge error."),
    };

    // ------------------------------------------------------------------ mode switch

    public async Task<NoiseControlResult> SetModeAsync(ModeSwitchRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        var hreq = new HandoverRequest(BridgeOperationKind.SwitchMode, request.Initiator, request.DeviceAddress, request.AllowInputLoss);
        if (Config.UseDaemon && ExtraArgs.Count == 0)
        {
            LastResult = null;
            var output = new StringBuilder();
            var r = await Coordinator.RunAsync(hreq, PrepareDaemonAsync,
                (s, c) => DaemonSwitchLoopAsync(s, request, progress, output, c), DaemonReleaseAsync, ct).ConfigureAwait(false);
            await CollectKernelEventsAsync(r).ConfigureAwait(false);
            lock (output) LastOutput = output.ToString();
            Finish(r);
            if (r.Completed) return r.Value!;
            if (r.Stage != HandoverStage.Prepare) return NotCompleted(r, request.Mode);
            _log.Warn("bridge daemon unavailable — using the per-command bridge.sh path: " + r.Error);
        }
        var args = new List<string> { "/opt/airpods/bridge.sh", "run", "--mac", BtAddress.Format(request.DeviceAddress),
            "--mode", ModeArg(request.Mode), "--hci-timeout", Inv(Config.HciTimeoutSeconds) };
        if (request.Mode == ListeningMode.Off && request.AutoEnableOffOption) args.Add("--allow-off");
        if (Trace) args.Add("--trace");
        args.AddRange(ExtraArgs);
        var (res, err, hr) = await RunLegacyAsync(hreq, args, progress, ct).ConfigureAwait(false);
        if (!hr.Completed) return NotCompleted(hr, request.Mode);
        if (res is null && err is not null) return new(NoiseControlOutcome.TransportError, request.Mode, null, err);
        return BridgeResultParser.ToNoiseControlResult(res, request.Mode);
    }

    private async Task<NoiseControlResult> DaemonSwitchLoopAsync(HandoverSession s, ModeSwitchRequest first, IProgress<string>? progress,
        StringBuilder output, CancellationToken ct)
    {
        var current = first;
        while (true)
        {
            s.Sending(current.Mode);
            var reply = await DaemonRequestAsync(s.Op, "switch", new Dictionary<string, object?>
            {
                ["mac"] = BtAddress.Format(current.DeviceAddress),
                ["mode"] = ModeArg(current.Mode),
                ["allow_off"] = current.Mode == ListeningMode.Off && current.AutoEnableOffOption,
                ["hci_timeout"] = Config.HciTimeoutSeconds,
                ["connect_timeout"] = 12,
                ["settle"] = 0.5,          // battery/ear arrive right after 0x0F; no 0x09 state comes without A2DP
                ["confirm_timeout"] = 0.2, // no echo comes without A2DP either (outcome "sent")
                ["trace"] = Trace,
            }, TimeSpan.FromSeconds(Config.HciTimeoutSeconds + 40), ct, progress, output).ConfigureAwait(false);
            if (reply is not { } rj)
                return new(NoiseControlOutcome.TransportError, current.Mode, null, "The WSL bridge daemon did not answer (see log).");
            var parsed = BridgeResultParser.Parse("RESULT " + rj.GetRawText());
            LastResult = parsed;
            var result = BridgeResultParser.ToNoiseControlResult(parsed, current.Mode);
            // never retried automatically: a missing echo ("sent") is the normal case without A2DP
            if (!result.Success) return result;
            if (s.NextOrClose() is not { } next) return result;
            current = next;
            progress?.Report($"Switching to {NoiseControlManager.Describe(next.Mode)}…");
        }
    }

    // ------------------------------------------------------------------ exact battery (AAP)

    /// <summary>Exact battery (1 %) over AAP: one hand-over, no command sent. Only on explicit user request.</summary>
    public async Task<(BridgeResult? result, string? error)> ReadBatteryAsync(ulong device, string initiator, IProgress<string>? progress,
        CancellationToken ct, bool allowInputLoss = false)
    {
        var hreq = new HandoverRequest(BridgeOperationKind.ReadBattery, initiator, device, allowInputLoss);
        if (Config.UseDaemon)
        {
            LastResult = null;
            var output = new StringBuilder();
            var r = await Coordinator.RunAsync(hreq, PrepareDaemonAsync, async (s, c) =>
            {
                var reply = await DaemonRequestAsync(s.Op, "read", new Dictionary<string, object?>
                {
                    ["mac"] = BtAddress.Format(device),
                    ["hci_timeout"] = Config.HciTimeoutSeconds,
                    ["connect_timeout"] = 12,
                    ["settle"] = 0.3,
                    ["listen"] = 1.5,
                    ["trace"] = Trace,
                }, TimeSpan.FromSeconds(Config.HciTimeoutSeconds + 40), c, progress, output).ConfigureAwait(false);
                return reply is { } rj ? BridgeResultParser.Parse("RESULT " + rj.GetRawText()) : null;
            }, DaemonReleaseAsync, ct).ConfigureAwait(false);
            await CollectKernelEventsAsync(r).ConfigureAwait(false);
            lock (output) LastOutput = output.ToString();
            Finish(r);
            if (r.Completed)
            {
                LastResult = r.Value;
                return r.Value is null ? (null, "The WSL bridge daemon did not answer (see log).")
                    : r.Value.Ok ? (r.Value, null) : (r.Value, r.Value.Error ?? "AirPods did not answer (" + r.Value.Outcome + ").");
            }
            if (r.Stage != HandoverStage.Prepare) return (null, r.Error);
        }
        var args = new List<string> { "/opt/airpods/bridge.sh", "run", "--mac", BtAddress.Format(device),
            "--hci-timeout", Inv(Config.HciTimeoutSeconds), "--settle", "1.5" };
        var (res, err, _) = await RunLegacyAsync(hreq, args, progress, ct).ConfigureAwait(false);
        return (res, err);
    }

    // ------------------------------------------------------------------ press-and-hold cycle (0x1A, + 0x34 when Off is in it), one hand-over

    /// <summary>
    /// Writes the press-and-hold cycle (0x1A) — and "Allow Off" (0x34) when Off is in it — in one hand-over and one AAP
    /// session. First listens for what the AirPods report (cycle, Allow Off, hold action), lets <see cref="OffCyclePlanner"/>
    /// decide — keep-current with an unreported cycle writes nothing — then writes each setting at most once.
    /// Never sends a listening mode (0x0D) or the hold action (0x16). Daemon only, no bridge.sh fallback.
    /// </summary>
    public async Task<OffCycleResult> ApplyHoldCycleAsync(OffCycleRequest request, AirPodsControl.Core.Models.AirPodsModelInfo? model,
        IProgress<string>? progress, CancellationToken ct)
    {
        if (OffCyclePlanner.ValidateRequest(request, model) is { } verr) return OffCycleResult.NotStarted(verr);
        if (!Config.UseDaemon) return OffCycleResult.NotStarted("Writing the press-and-hold cycle needs the WSL bridge daemon (\"UseDaemon\" is off in bridge.json).");
        LastResult = null;
        var output = new StringBuilder();
        var hreq = new HandoverRequest(BridgeOperationKind.HoldCycle, request.Initiator, request.Device, request.AllowInputLoss);
        var r = await Coordinator.RunAsync(hreq, PrepareDaemonAsync, async (s, c) =>
        {
            var mac = BtAddress.Format(request.Device);
            s.Op.App("offcycle_request", request.Modes is { } em ? "set " + CycleModeSet.ToArgs(em) : "keep current + off");
            var read = await DaemonRequestAsync(s.Op, "read", new Dictionary<string, object?>
            {
                ["mac"] = mac,
                ["hci_timeout"] = Config.HciTimeoutSeconds,
                ["connect_timeout"] = 12,
                ["settle"] = 0.5,
                ["listen"] = 3.0,   // with an audio link the control state came ~2-3 s after 0x0F; without one nothing comes
                ["wait_controls"] = new[] { (int)ControlId.ListeningModeConfigs, ControlId.AllowOffOption, ControlId.ClickHoldMode },
                ["trace"] = Trace,
            }, TimeSpan.FromSeconds(Config.HciTimeoutSeconds + 40), c, progress, output).ConfigureAwait(false);
            if (read is not { } rj) return OffCycleResult.Failed(OffCycleState.Unknown, null, "The WSL bridge daemon did not answer (see log). Nothing was sent.");
            var parsed = BridgeResultParser.Parse("RESULT " + rj.GetRawText());
            LastResult = parsed;   // battery from this same session is shown by the controller — no extra hand-over for it
            if (parsed is not { Ok: true })
                return OffCycleResult.Failed(OffCycleState.Unknown, null, parsed?.Outcome == "no_response"
                    ? "The AirPods did not answer the AAP handshake. Nothing was sent."
                    : BridgeResultParser.ToNoiseControlResult(parsed, ListeningMode.Off).Message + " Nothing was sent.");

            var state = OffCycleReplies.ParseState(rj.GetRawText());
            s.Op.App("offcycle_state", $"cycle={(state.Cycle is { } cy ? $"0x{(byte)cy:X2}" : "unreported")} allow_off={state.AllowOff?.ToString() ?? "unreported"} " +
                                       $"hold={(state.Hold is { } h ? $"right=0x{h.Right:X2},left=0x{h.Left:X2}" : "unreported")}");
            if (state.Cycle is { } reportedCycle)
                Config.HoldCycleReported = new OffCycleRecord { Device = request.Device.ToString("X12"), Modes = (byte)reportedCycle, At = DateTimeOffset.Now, Status = "Reported", OpId = s.Op.Id };
            var plan = OffCyclePlanner.Plan(request, state, model);
            s.Op.App("offcycle_plan", plan.Proceed
                ? $"allow_off={(plan.SendAllowOff ? "send" : "already")} cycle={(plan.SendCycle is { } sc ? $"send 0x{(byte)sc:X2}" : "already")}"
                : "stop: " + plan.StopReason);
            if (!plan.Proceed) return OffCycleResult.Build(plan, state, SettingOutcome.NotSent, SettingOutcome.NotSent);
            if (plan.NothingToSend) return OffCycleResult.Build(plan, state, SettingOutcome.AlreadySet, SettingOutcome.AlreadySet);

            var cfg = await DaemonRequestAsync(s.Op, "configure", new Dictionary<string, object?>
            {
                ["mac"] = mac,
                ["allow_off"] = plan.SendAllowOff,
                ["cycle"] = plan.SendCycle is { } m ? (int)m : null,
                ["confirm_timeout"] = 1.0,
            }, TimeSpan.FromSeconds(15), c, progress, output).ConfigureAwait(false);
            if (cfg is not { } cj)
                return OffCycleResult.Build(plan, state, plan.SendAllowOff ? SettingOutcome.Unknown : SettingOutcome.AlreadySet,
                    plan.SendCycle is null ? SettingOutcome.AlreadySet : SettingOutcome.Unknown);
            var outcome = cj.TryGetProperty("outcome", out var ov) ? ov.GetString() : null;
            if (outcome is "bad_arguments" or "no_channel" or "no_response")
                return OffCycleResult.Failed(state, plan.Target, "The bridge refused the request (" + outcome + "): " +
                    (cj.TryGetProperty("error", out var ev) ? ev.GetString() : "?") + ". Nothing was sent.");
            var (ao, cyr) = OffCycleReplies.ParseSettings(cj.GetRawText());
            var aoOut = plan.SendAllowOff ? ao?.Outcome ?? SettingOutcome.Unknown : SettingOutcome.AlreadySet;
            var cyOut = plan.SendCycle is null ? SettingOutcome.AlreadySet : cyr?.Outcome ?? SettingOutcome.Unknown;
            return OffCycleResult.Build(plan, state, aoOut, cyOut, ao?.Echo, cyr?.Echo, ao?.Error ?? cyr?.Error);
        }, DaemonReleaseAsync, ct).ConfigureAwait(false);
        await CollectKernelEventsAsync(r).ConfigureAwait(false);
        lock (output) LastOutput = output.ToString();
        Finish(r);

        var result = r.Completed ? r.Value! : r.Stage switch
        {
            HandoverStage.Busy or HandoverStage.Lock or HandoverStage.Adapter or HandoverStage.InputGuard => OffCycleResult.NotStarted(r.Error ?? "Not started."),
            HandoverStage.Prepare => OffCycleResult.NotStarted("The WSL bridge daemon is not available: " + r.Error),
            _ => OffCycleResult.Failed(OffCycleState.Unknown, null, (r.Error ?? "WSL bridge error.") + " Nothing was retried."),
        };
        r.Op.App("offcycle_result", $"{result.Status}: allow_off={result.AllowOff} cycle={result.Cycle}");
        if ((result.AnythingSent || result.Status == OffCycleStatus.AlreadySet) && result.Target is { } t)
            Config.LastOffCycle = new OffCycleRecord { Device = request.Device.ToString("X12"), Modes = (byte)t, At = DateTimeOffset.Now, Status = result.Status.ToString(), OpId = r.Op.Id };
        try { Config.Save(); } catch (Exception ex) { _log.Warn("Could not save bridge config", ex); }
        return result;
    }

    // ------------------------------------------------------------------ wizard tests (bridge.sh)

    /// <summary>Read-only session (current mode, exact battery) — used by the setup wizard's end-to-end test.</summary>
    public async Task<(BridgeResult? result, string? error)> ReadStatusAsync(ulong device, IProgress<string>? progress, CancellationToken ct,
        double settleSeconds = 3, bool trace = false, string initiator = "wizard")
    {
        var args = new List<string> { "/opt/airpods/bridge.sh", "run", "--mac", BtAddress.Format(device),
            "--hci-timeout", Inv(Config.HciTimeoutSeconds), "--settle", Inv(settleSeconds) };
        if (trace) args.Add("--trace");
        var (res, err, _) = await RunLegacyAsync(new HandoverRequest(BridgeOperationKind.AirPodsTest, initiator, device), args, progress, ct).ConfigureAwait(false);
        return (res, err);
    }

    /// <summary>Adapter-only test: attach, wait for hci0, power on BlueZ, optional LE scan (Apple proximity beacons), detach.</summary>
    public async Task<(BridgeResult? result, string? error)> TestAdapterAsync(IProgress<string>? progress, CancellationToken ct, int scanSeconds = 0,
        string initiator = "wizard")
    {
        var args = new List<string> { "/opt/airpods/bridge.sh", "adapter-test", "--hci-timeout", Inv(Math.Max(Config.HciTimeoutSeconds, 8)) };
        if (scanSeconds > 0) args.AddRange(["--scan", scanSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        var (res, err, _) = await RunLegacyAsync(new HandoverRequest(BridgeOperationKind.AdapterTest, initiator, CurrentDevice), args, progress, ct).ConfigureAwait(false);
        return (res, err);
    }

    /// <summary>Checks only (no hand-over): which adapter would be used and whether input devices would be affected.</summary>
    public async Task<(AdapterResolution adapter, InputGuardReport? input)> CheckAsync(ulong? device, CancellationToken ct)
    {
        var op = Coordinator.NewTrace(BridgeOperationKind.SwitchMode, "check");
        var res = await ResolveAdapterAsync(op, ct).ConfigureAwait(false);
        if (!res.Ok) return (res, null);
        return (res, await CheckInputDevicesAsync(res.Target!, device, op, ct).ConfigureAwait(false));
    }

    // ------------------------------------------------------------------ daemon plumbing

    private async Task<string?> PrepareDaemonAsync(OperationTrace op, CancellationToken ct)
    {
        if (await EnsureScriptsAsync(ct).ConfigureAwait(false) is { } serr) return "script update failed: " + serr;
        var derr = await _daemon.EnsureStartedAsync(Config.DaemonIdleMinutes, TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
        op.App("daemon_ready", derr ?? "ok");
        return derr;
    }

    private async Task<JsonElement?> DaemonRequestAsync(OperationTrace op, string name, Dictionary<string, object?> fields, TimeSpan timeout,
        CancellationToken ct, IProgress<string>? progress = null, StringBuilder? output = null)
    {
        fields["op_id"] = op.Id;
        double sentAt = op.Now;
        op.App("daemon_" + name, fields.TryGetValue("mode", out var m) && m is not null ? "mode=" + m : null);
        var reply = await _daemon.RequestAsync(name, fields, timeout, ct, ev => OnDaemonEvent(op, sentAt, ev, progress, output)).ConfigureAwait(false);
        if (reply is not { } rj) { op.App("daemon_no_reply", name); return null; }
        if (rj.TryGetProperty("rx", out var rx) && rx.ValueKind == JsonValueKind.Array)
            foreach (var e in rx.EnumerateArray())
                if (e.GetArrayLength() >= 2) op.Observed(TraceOrigin.Aap, "rx", e[1].GetString(), sentAt + e[0].GetDouble());
        var outcome = rj.TryGetProperty("outcome", out var o) ? o.GetString() : null;
        op.App("daemon_reply", $"{name}: {outcome}" + (rj.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String ? " — " + er.GetString() : ""));
        if (output is not null) lock (output) output.Append("RESULT ").AppendLine(rj.GetRawText());
        _log.Info($"bridge {op.Id} {name}: {rj.GetRawText()}");
        return rj;
    }

    private static void OnDaemonEvent(OperationTrace op, double sentAt, JsonElement ev, IProgress<string>? progress, StringBuilder? output)
    {
        var type = ev.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type == "pkt")
        {
            var line = ev.TryGetProperty("line", out var l) ? l.GetString() : "";
            if (output is not null) lock (output) output.Append("PKT ").AppendLine(line);
            if (line is not null && line.StartsWith("tx ", StringComparison.Ordinal)) op.Observed(TraceOrigin.Aap, "tx", line[3..]);
            return;
        }
        var stage = ev.TryGetProperty("stage", out var s) ? s.GetString() ?? "" : "";
        double at = ev.TryGetProperty("t", out var tt) && tt.ValueKind == JsonValueKind.Number ? sentAt + tt.GetDouble() : op.Now;
        int sp = stage.IndexOf(' ');
        op.Observed(TraceOrigin.Daemon, sp < 0 ? stage : stage[..sp], sp < 0 ? null : stage[(sp + 1)..], at);
        if (output is not null) lock (output) output.Append("STAGE ").AppendLine(stage);
        progress?.Report(stage switch
        {
            "hci" => "Adapter is up in WSL…",
            "keys" => "Loading the pairing key…",
            "powered" => "Connecting to AirPods (L2CAP 0x1001)…",
            var x when x.StartsWith("connect_failed", StringComparison.Ordinal) => "AirPods refused the connection, retrying…",
            var x when x.StartsWith("connect", StringComparison.Ordinal) => "Connecting to AirPods (L2CAP 0x1001)…",
            "handshake" => "AAP handshake…",
            "allow_off" => "Allowing Off on the AirPods…",
            "wait_controls" => "Reading the AirPods' press-and-hold settings…",
            "cycle" => "Writing the press-and-hold cycle…",
            "configured" => "Finishing…",
            "set_mode" => "Sending mode…",
            "sent" => "Mode sent.",
            "listened" => "Battery received.",
            var x => x,
        });
    }

    /// <summary>Closes the AAP channel and drops the ACL link before usbipd detach: the AirPods then know at once that
    /// this host is gone (instead of a link-supervision timeout).</summary>
    private async Task DaemonReleaseAsync(HandoverSession s) =>
        await DaemonRequestAsync(s.Op, "release", new Dictionary<string, object?>(), TimeSpan.FromSeconds(6), CancellationToken.None).ConfigureAwait(false);

    /// <summary>After detach: the kernel's view of this operation (ACL connect/disconnect with reasons, adapter index).</summary>
    private async Task CollectKernelEventsAsync<T>(HandoverResult<T> r)
    {
        if (!r.Disrupted || !_daemon.IsRunning) return;
        var op = r.Op;
        double sentAt = op.Now;
        var reply = await _daemon.RequestAsync("events", new Dictionary<string, object?> { ["op_id"] = op.Id }, TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        if (reply is not { } rj) return;
        if (rj.TryGetProperty("monitor_error", out var me) && me.ValueKind == JsonValueKind.String) op.App("kernel_events_unavailable", me.GetString());
        if (rj.TryGetProperty("events", out var evs) && evs.ValueKind == JsonValueKind.Array)
            foreach (var e in evs.EnumerateArray())
                if (e.GetArrayLength() >= 3)
                    op.Observed(TraceOrigin.Linux, e[1].GetString() ?? "?", e[2].ValueKind == JsonValueKind.String ? e[2].GetString() : null, sentAt + e[0].GetDouble());
    }

    /// <summary>Pushes newer embedded scripts into the distribution (no admin rights needed). Returns an error or null.</summary>
    private async Task<string?> EnsureScriptsAsync(CancellationToken ct)
    {
        if (Config.ScriptVersion >= BridgeConfig.CurrentScriptVersion) return null;
        _log.Info($"bridge scripts v{Config.ScriptVersion} → v{BridgeConfig.CurrentScriptVersion}: updating /opt/airpods");
        var err = await BridgeScripts.CopyAsync(_wsl, Config.Distro, ct).ConfigureAwait(false);
        if (err is not null) return err;
        _daemon.Stop();   // an old daemon would keep running the old code
        Config.ScriptVersion = BridgeConfig.CurrentScriptVersion;
        Config.Save();
        return null;
    }

    // ------------------------------------------------------------------ per-command path (wizard tests, fallback)

    private async Task<(BridgeResult? result, string? error, HandoverResult<(BridgeResult?, string?)> handover)> RunLegacyAsync(
        HandoverRequest request, IReadOnlyList<string> bridgeArgs, IProgress<string>? progress, CancellationToken ct)
    {
        LastResult = null; // never let a previous session's battery/mode leak into this one
        Process? keepAlive = null;
        try
        {
            var r = await Coordinator.RunAsync<(BridgeResult?, string?)>(request, async (op, c) =>
            {
                keepAlive = _wsl.StartKeepAlive(Config.Distro, 120);
                var warm = await _wsl.ExecAsync(Config.Distro, ["true"], TimeSpan.FromSeconds(30), c).ConfigureAwait(false);
                if (!warm.Success) return "WSL distribution did not start: " + warm.FirstErrorLine;
                if (await EnsureScriptsAsync(c).ConfigureAwait(false) is { } serr) _log.Warn("script update failed: " + serr);
                return null;
            }, async (s, c) =>
            {
                s.Op.App("bridge_sh", string.Join(' ', bridgeArgs.Skip(1).Take(1)));
                var run = await _wsl.ExecAsync(Config.Distro, bridgeArgs, TimeSpan.FromSeconds(60), c, onLine: line =>
                {
                    if (!line.StartsWith("STAGE ", StringComparison.Ordinal)) return;
                    var st = line[6..];
                    int sp = st.IndexOf(' ');
                    s.Op.Observed(TraceOrigin.Daemon, sp < 0 ? st : st[..sp], sp < 0 ? null : st[(sp + 1)..]);
                    progress?.Report(st switch
                    {
                        "wait_hci" => "Waiting for the adapter in WSL…",
                        "bluez" => "Starting BlueZ…",
                        "scan" => "Scanning for AirPods (LE)…",
                        "aap" => "Connecting to AirPods (L2CAP 0x1001)…",
                        "a2dp" => "Connecting audio (A2DP) — AirPods only accept noise control from an audio source…",
                        var x when x.StartsWith("connect") => "Connecting to AirPods (L2CAP 0x1001)…",
                        "handshake" => "AAP handshake…",
                        "allow_off" => "Allowing Off on the AirPods…",
                        "set_mode" => "Sending mode, waiting for confirmation…",
                        var x => x,
                    });
                }).ConfigureAwait(false);
                var parsed = BridgeResultParser.Parse(run.StdOut);
                LastResult = parsed;
                LastOutput = run.StdOut;
                _log.Info($"bridge {s.Op.Id} result: {(parsed?.Raw ?? "(none)")}{(run.TimedOut ? " [timed out]" : "")}");
                return parsed is null
                    ? (null, run.TimedOut ? "WSL bridge timed out." : "WSL bridge failed: " + run.FirstErrorLine)
                    : (parsed, null);
            }, null, ct).ConfigureAwait(false);
            Finish(r);
            return r.Completed ? (r.Value.Item1, r.Value.Item2, r) : (null, r.Error, r);
        }
        finally
        {
            try { keepAlive?.Kill(entireProcessTree: true); } catch { }
            keepAlive?.Dispose();
        }
    }

    // ------------------------------------------------------------------ IHandoverHost: the Windows side of a hand-over

    IDisposable? IHandoverHost.TryAcquireSystemLock(OperationTrace op, out string? holder) => BridgeOperationLock.TryAcquire(op.Header, out holder);

    public async Task<AdapterResolution> ResolveAdapterAsync(OperationTrace op, CancellationToken ct)
    {
        if (Config.AdapterVidPid is not { } vp) return AdapterResolution.Fail("Bridge not set up (no adapter selected).");
        if (!_usbipd.IsInstalled) return AdapterResolution.Fail("usbipd-win is not installed (run Driverless setup).");
        IReadOnlyList<UsbipdDevice> devices;
        try { devices = await _usbipd.GetDevicesAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { return AdapterResolution.Fail("usbipd failed: " + ex.Message); }
        WindowsRadioInfo? radio;
        try { radio = await BluetoothRadioSwitch.GetWindowsRadioAsync().ConfigureAwait(false); }
        catch (Exception ex) { return AdapterResolution.Fail("Could not query Windows' Bluetooth adapter (" + ex.Message + "); stopped before switching anything off."); }
        op.App("windows_radio", radio is null ? "none" : $"{radio.DeviceId} chain={string.Join(" < ", radio.InstanceChain.Take(2))}");
        var res = AdapterMatcher.Resolve(vp, Config.AdapterUsbInstanceId, devices, radio);
        if (res.Ok && Config.AdapterUsbInstanceId is null)
        {
            Config.AdapterUsbInstanceId = res.Target!.UsbInstanceId;   // pin the exact device from now on
            Config.Save();
        }
        return res;
    }

    public async Task<InputGuardReport> CheckInputDevicesAsync(AdapterTarget target, ulong? excludeAddress, OperationTrace op, CancellationToken ct)
    {
        try
        {
            var report = await InputDeviceScanner.ScanAsync(target, excludeAddress, _log).ConfigureAwait(false);
            foreach (var d in report.Devices)
                op.App("input_device", $"{d.Name} {d.Transport} connected={d.Connected?.ToString() ?? "unknown"} on_adapter={d.OnTargetAdapter?.ToString() ?? "unknown"} ({d.Evidence})");
            return report;
        }
        catch (Exception ex)
        {
            return InputGuardPolicy.Evaluate([], ["input device scan failed: " + ex.Message]);
        }
    }

    Task<bool> IHandoverHost.ConfirmInputLossAsync(InputGuardReport report, OperationTrace op) =>
        ConfirmInputLoss is { } ask ? ask(report, op) : Task.FromResult(false);

    async Task<(bool turnedOff, string? error)> IHandoverHost.RadioOffAsync(AdapterTarget target, OperationTrace op)
    {
        if (!target.RadioBelongsToAdapter || target.WindowsRadioDeviceId is not { } id)
        {
            op.App("radio_untouched", "Windows' active radio is not this adapter");
            return (false, null);
        }
        var state = await _radio.GetStateAsync(id).ConfigureAwait(false);
        if (state != RadioState.On)
        {
            op.App("radio_already_off", state?.ToString() ?? "unknown");
            return (false, null);
        }
        Config.RadioTurnedOff = true; Config.RadioDeviceId = id; Config.Save();   // crash safety: RecoverAsync turns it back on
        op.App("radio_off_request", id);
        var err = await _radio.SetAsync(id, RadioState.Off).ConfigureAwait(false);
        op.App(err is null ? "radio_off" : "radio_off_failed", err);
        if (err is not null) { Config.RadioTurnedOff = false; Config.Save(); return (false, err); }
        return (true, null);
    }

    /// <summary>Attaches as soon as Windows has released the adapter: retries "busy" for up to 8 s instead of a fixed pause.</summary>
    async Task<string?> IHandoverHost.AttachAsync(AdapterTarget target, OperationTrace op, CancellationToken ct)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        int attempt = 0;
        while (true)
        {
            attempt++;
            op.App("attach_start", $"busid={target.BusId} attempt={attempt}");
            var att = await _usbipd.AttachAsync(target.BusId, Config.Distro, ct).ConfigureAwait(false);
            if (att.Success) { op.App("attach_ok"); return null; }
            var why = att.Combined.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("warning", StringComparison.OrdinalIgnoreCase))
                      ?? att.FirstErrorLine;
            bool busy = att.Combined.Contains("busy", StringComparison.OrdinalIgnoreCase) || att.Combined.Contains("used by", StringComparison.OrdinalIgnoreCase);
            op.App("attach_failed", why);
            if (!busy || DateTime.UtcNow > until) return "usbipd attach failed: " + why;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    async Task IHandoverHost.DetachAsync(AdapterTarget target, OperationTrace op)
    {
        var det = await _usbipd.DetachAsync(target.BusId, CancellationToken.None).ConfigureAwait(false);
        if (!det.Success)
        {
            // by bus id again after a moment — never "by hardware id", which would also hit an identical second adapter
            op.App("detach_retry", det.FirstErrorLine);
            await Task.Delay(500).ConfigureAwait(false);
            det = await _usbipd.DetachAsync(target.BusId, CancellationToken.None).ConfigureAwait(false);
        }
        op.App("detach", det.Success ? "ok" : det.FirstErrorLine);
    }

    async Task IHandoverHost.RadioOnAsync(AdapterTarget target, OperationTrace op)
    {
        var id = target.WindowsRadioDeviceId ?? Config.RadioDeviceId;
        if (id is null) return;
        var on = await _radio.RestoreOnAsync(id, TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false);
        op.App(on is null ? "radio_on" : "radio_on_failed", on);
        if (on is null) { Config.RadioTurnedOff = false; Config.Save(); }
        else _log.Warn("Bluetooth radio not restored: " + on);
    }

    void IHandoverHost.OperationStarted(OperationTrace op, ulong? device)
    {
        _probe?.Dispose();
        _probe = new HandoverProbe(op, device ?? CurrentDevice, _log);
        _probe.Start();
    }

    void IHandoverHost.OperationFinished(OperationTrace op) => _probe?.Finish();

    // ------------------------------------------------------------------ recovery

    /// <summary>
    /// Safety net at startup: if the app (or PC) died while the adapter was handed to WSL, give it back to Windows.
    /// Runs exclusively: never while this or another process (CLI) is in the middle of a hand-over.
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        if (Config.AdapterVidPid is not { } vp || !_usbipd.IsInstalled) return;
        try
        {
            bool ran = await Coordinator.TryRunExclusiveAsync(BridgeOperationKind.Recovery, "startup", async op =>
            {
                var devices = await _usbipd.GetDevicesAsync(ct).ConfigureAwait(false);
                foreach (var dev in devices.Where(d => d.Matches(vp) && d.IsAttached && d.BusId is not null
                             && (Config.AdapterUsbInstanceId is null || string.Equals(d.InstanceId, Config.AdapterUsbInstanceId, StringComparison.OrdinalIgnoreCase))))
                {
                    _log.Warn($"Bluetooth adapter {dev.BusId} was still attached to WSL — detaching (recovery)");
                    op.App("recovery_detach", dev.BusId);
                    await _usbipd.DetachAsync(dev.BusId!, ct).ConfigureAwait(false);
                }
                if (Config.RadioTurnedOff)
                {
                    _log.Warn("Bluetooth radio was left off by an interrupted bridge session — turning it back on (recovery)");
                    var id = Config.RadioDeviceId ?? (await BluetoothRadioSwitch.GetWindowsRadioAsync().ConfigureAwait(false))?.DeviceId;
                    var err = id is null ? "no Windows Bluetooth adapter" : await _radio.RestoreOnAsync(id, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                    op.App(err is null ? "recovery_radio_on" : "recovery_radio_failed", err);
                    if (err is null) { Config.RadioTurnedOff = false; Config.Save(); }
                }
            }).ConfigureAwait(false);
            if (!ran) _log.Info("bridge recovery skipped: another bridge operation is running");
        }
        catch (Exception ex) { _log.Warn("Bridge recovery check failed", ex); }
    }
}
