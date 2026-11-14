using System.Globalization;
using System.Numerics;
using AirPodsControl.Core.Models;
using AirPodsControl.Core.Protocol;

namespace AirPodsControl.Core.Services;

/// <summary>
/// Modes in the AirPods' press-and-hold cycle: the bitmask of control 0x1A "ListeningModeConfigs"
/// (LibrePods docs/control_commands.md: Off 0x01, ANC 0x02, Transparency 0x04, Adaptive 0x08).
/// Not the same numbers as the listening-mode values of control 0x0D (1..4).
/// </summary>
[Flags]
public enum CycleModes : byte
{
    None = 0,
    Off = 0x01,
    NoiseCancellation = 0x02,
    Transparency = 0x04,
    Adaptive = 0x08,
}

public static class CycleModeSet
{
    public const CycleModes All = CycleModes.Off | CycleModes.NoiseCancellation | CycleModes.Transparency | CycleModes.Adaptive;

    private static readonly (CycleModes flag, string arg, string name)[] Names =
    [
        (CycleModes.Off, "off", "Off"),
        (CycleModes.NoiseCancellation, "anc", "Noise Cancellation"),
        (CycleModes.Transparency, "transparency", "Transparency"),
        (CycleModes.Adaptive, "adaptive", "Adaptive"),
    ];

    public static int Count(CycleModes m) => BitOperations.PopCount((uint)m);

    public static string Describe(CycleModes m)
    {
        var parts = Names.Where(n => m.HasFlag(n.flag)).Select(n => n.name).ToList();
        if (((byte)m & ~(byte)All) != 0) parts.Add($"unknown bits 0x{(byte)m & ~(byte)All:X2}");
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    public static string ToArgs(CycleModes m) => string.Join(",", Names.Where(n => m.HasFlag(n.flag)).Select(n => n.arg));

    /// <summary>"off,anc,transparency,adaptive" (CLI). Returns null and an error for unknown names or an empty list.</summary>
    public static CycleModes? Parse(string? list, out string? error)
    {
        error = null;
        var result = CycleModes.None;
        foreach (var raw in (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var hit = Names.FirstOrDefault(n => n.arg.Equals(raw, StringComparison.OrdinalIgnoreCase)
                                                || (raw.Equals("nc", StringComparison.OrdinalIgnoreCase) && n.flag == CycleModes.NoiseCancellation));
            if (hit.flag == CycleModes.None) { error = $"unknown mode '{raw}' (use off, anc, transparency, adaptive)"; return null; }
            result |= hit.flag;
        }
        if (result == CycleModes.None) { error = "no modes given"; return null; }
        return result;
    }

    /// <summary>
    /// Whether this set may be written as the press-and-hold cycle. Null = valid.
    ///  * only the four documented bits;
    ///  * at least two modes: every 0x1A value in the protocol notes has two or more bits set, and a one-mode
    ///    cycle would make the press-and-hold gesture do nothing — not something to write without a source;
    ///  * only modes the model has (Adaptive; Off only where Off exists).
    /// </summary>
    public static string? Validate(CycleModes m, AirPodsModelInfo? model)
    {
        if (((byte)m & ~(byte)All) != 0) return $"Unknown cycle bits 0x{(byte)m:X2}.";
        if (Count(m) < 2) return "Choose at least two modes: press and hold switches between them.";
        if (model is not null)
        {
            if (!model.HasNoiseControl) return $"{model.Name} has no listening modes.";
            if (m.HasFlag(CycleModes.Adaptive) && model.Adaptive == Support.No) return $"{model.Name} does not support Adaptive.";
            if (m.HasFlag(CycleModes.Off) && model.OffMode == OffModeRequirement.NotApplicable) return $"{model.Name} has no Off mode.";
        }
        return null;
    }
}

public enum HoldAction { NoiseControl, Siri, Other }

/// <summary>Press-and-hold action per stem as reported by control 0x16 (data1 = right, data2 = left). Never written by this app.</summary>
public sealed record HoldAssignment(byte Right, byte Left)
{
    public static HoldAction Decode(byte v) => v switch { 0x01 => HoldAction.NoiseControl, 0x05 => HoldAction.Siri, _ => HoldAction.Other };
    public HoldAction RightAction => Decode(Right);
    public HoldAction LeftAction => Decode(Left);

    private static string Name(byte v) => Decode(v) switch
    {
        HoldAction.NoiseControl => "noise control",
        HoldAction.Siri => "Siri",
        _ => $"unknown (0x{v:X2})",
    };

    public string Describe() => $"left stem: {Name(Left)}, right stem: {Name(Right)}";
}

/// <summary>What the AirPods reported in the AAP session before anything was written (null = not reported).</summary>
public sealed record OffCycleState(bool? AllowOff, CycleModes? Cycle, HoldAssignment? Hold)
{
    public static readonly OffCycleState Unknown = new(null, null, null);
}

/// <param name="Modes">The exact press-and-hold cycle wanted (Off is a mode like the others; choosing it also allows Off).
/// Null = keep the cycle the AirPods report and add Off (CLI "enable Off") — only possible if they report it.</param>
public sealed record OffCycleRequest(ulong Device, CycleModes? Modes, string Initiator = "app", bool AllowInputLoss = false)
{
    public bool KeepCurrentAddOff => Modes is null;
}

/// <summary>What to write in the session. <see cref="StopReason"/> set = write nothing.</summary>
public sealed record OffCyclePlan(bool SendAllowOff, CycleModes? SendCycle, CycleModes? Target, bool TargetFromDevice, string? StopReason)
{
    public bool Proceed => StopReason is null;
    public bool NothingToSend => !SendAllowOff && SendCycle is null;

    public static OffCyclePlan Stop(string reason, CycleModes? target = null) => new(false, null, target, false, reason);
}

public static class OffCyclePlanner
{
    public const string CurrentUnknownMessage =
        "The AirPods did not report the modes currently in their press-and-hold cycle, so nothing was changed. " +
        "Choose the modes explicitly and send again.";

    /// <summary>Checks done before any hand-over (null = fine).</summary>
    public static string? ValidateRequest(OffCycleRequest r, AirPodsModelInfo? model)
    {
        if (model is not null && !model.HasNoiseControl) return $"{model.Name} has no listening modes.";
        if (r.KeepCurrentAddOff && model?.OffMode == OffModeRequirement.NotApplicable) return $"{model.Name} has no Off mode.";
        return r.Modes is { } m ? CycleModeSet.Validate(m, model) : null;
    }

    /// <summary>
    /// Decides what to write from the request and what the AirPods reported in this session.
    ///  * Off in the target → "Allow Off" (0x34 = 01) first, unless the AirPods reported it as allowed. Without Off, 0x34 is not touched.
    ///  * Never guesses the current cycle: keep-current with an unreported cycle writes nothing.
    ///  * Values the AirPods already reported are not written again.
    ///  * The listening mode itself (0x0D) and the hold action (0x16) are never part of it.
    /// </summary>
    public static OffCyclePlan Plan(OffCycleRequest r, OffCycleState s, AirPodsModelInfo? model)
    {
        CycleModes target;
        bool fromDevice;
        if (r.Modes is { } m) { target = m; fromDevice = false; }
        else if (s.Cycle is { } c) { target = c | CycleModes.Off; fromDevice = true; }
        else return OffCyclePlan.Stop(CurrentUnknownMessage);

        if (CycleModeSet.Validate(target, model) is { } err)
            return OffCyclePlan.Stop((fromDevice ? $"The AirPods reported the cycle {CycleModeSet.Describe(s.Cycle!.Value)}; adding Off gives an invalid set: " : "") + err, target);
        bool allowOff = target.HasFlag(CycleModes.Off) && s.AllowOff != true;
        return new(allowOff, s.Cycle == target ? null : target, target, fromDevice, null);
    }
}

public enum SettingOutcome
{
    /// <summary>Not written: the AirPods already reported this value in the session (or it was not needed).</summary>
    AlreadySet,
    /// <summary>Not written (planned stop, or an earlier command failed / was refused).</summary>
    NotSent,
    /// <summary>Writing to the channel failed.</summary>
    SendFailed,
    /// <summary>Written on an acknowledged channel; the AirPods sent no answer. Not proof that it was applied.</summary>
    Sent,
    /// <summary>The AirPods answered with the requested value.</summary>
    Confirmed,
    /// <summary>The AirPods answered with another value.</summary>
    DeviceReportedDifferent,
    /// <summary>The bridge did not answer: whether it was written is not known.</summary>
    Unknown,
}

public enum OffCycleStatus
{
    /// <summary>Stopped before Bluetooth was touched (busy, input devices, adapter, invalid set, bridge not ready).</summary>
    NotStarted,
    /// <summary>Session ran, nothing was written (current set not reported and none chosen, or invalid result).</summary>
    NothingSent,
    /// <summary>Everything requested was already reported as set; nothing written.</summary>
    AlreadySet,
    /// <summary>Written, at least one setting without an answer from the AirPods.</summary>
    Sent,
    /// <summary>Every written setting was answered with the requested value.</summary>
    Confirmed,
    /// <summary>The AirPods answered with a different value.</summary>
    Refused,
    Failed,
}

/// <summary>
/// Outcome of writing the press-and-hold cycle. Keeps three facts apart: whether commands were sent, whether the AirPods
/// confirmed them, and whether the setting survived a reconnect — the last one the app cannot observe
/// (<see cref="PersistenceVerified"/> is always false; it is checked by holding a stem after reconnecting).
/// </summary>
public sealed record OffCycleResult(OffCycleStatus Status, OffCycleState Before, CycleModes? Target, SettingOutcome AllowOff,
    SettingOutcome Cycle, string Message, string? Error = null)
{
    public const string SentMessage = "Settings sent. Check whether Off now appears when you press and hold a stem.";
    public const string SentMessageNoOff = "Settings sent. Press and hold a stem to check which modes it now switches between.";

    public bool PersistenceVerified => false;
    public bool Success => Status is OffCycleStatus.Sent or OffCycleStatus.Confirmed or OffCycleStatus.AlreadySet;
    /// <summary>At least one command was (or may have been) written to the AirPods.</summary>
    public bool AnythingSent => Written(AllowOff) || Written(Cycle);

    private static bool Written(SettingOutcome o) => o is SettingOutcome.Sent or SettingOutcome.Confirmed or SettingOutcome.DeviceReportedDifferent or SettingOutcome.Unknown;

    public static OffCycleResult NotStarted(string message) =>
        new(OffCycleStatus.NotStarted, OffCycleState.Unknown, null, SettingOutcome.NotSent, SettingOutcome.NotSent, message);

    public static OffCycleResult Failed(OffCycleState before, CycleModes? target, string message, SettingOutcome allowOff = SettingOutcome.NotSent,
        SettingOutcome cycle = SettingOutcome.NotSent) => new(OffCycleStatus.Failed, before, target, allowOff, cycle, message, message);

    public static OffCycleResult Build(OffCyclePlan plan, OffCycleState before, SettingOutcome allowOff, SettingOutcome cycle,
        byte? allowOffEcho = null, byte? cycleEcho = null, string? error = null)
    {
        if (!plan.Proceed)
            return new(OffCycleStatus.NothingSent, before, plan.Target, SettingOutcome.NotSent, SettingOutcome.NotSent, plan.StopReason!);
        var t = plan.Target!.Value;
        var target = CycleModeSet.Describe(t);
        if (allowOff == SettingOutcome.Unknown || cycle == SettingOutcome.Unknown)
            return new(OffCycleStatus.Failed, before, plan.Target, allowOff, cycle,
                "The WSL bridge did not answer, so whether the settings were written is unknown. Nothing was retried; hold a stem to check.", error);
        if (allowOff == SettingOutcome.SendFailed || cycle == SettingOutcome.SendFailed)
            return new(OffCycleStatus.Failed, before, plan.Target, allowOff, cycle,
                (allowOff is SettingOutcome.Sent or SettingOutcome.Confirmed ? "'Allow Off' was sent, but sending the cycle failed" : "Sending failed") +
                $": {error ?? "channel error"}. Nothing was retried.", error);
        if (allowOff == SettingOutcome.DeviceReportedDifferent)
            return new(OffCycleStatus.Refused, before, plan.Target, allowOff, cycle,
                $"The AirPods answered 'Allow Off' with 0x{allowOffEcho:X2} instead of 0x01 (allowed); the cycle was not sent.");
        if (cycle == SettingOutcome.DeviceReportedDifferent)
            return new(OffCycleStatus.Refused, before, plan.Target, allowOff, cycle,
                $"The AirPods answered with the cycle {(cycleEcho is { } ce ? CycleModeSet.Describe((CycleModes)ce) : "?")} instead of {target}.");
        if (allowOff == SettingOutcome.AlreadySet && cycle == SettingOutcome.AlreadySet)
            return new(OffCycleStatus.AlreadySet, before, plan.Target, allowOff, cycle,
                $"The AirPods report that the cycle is already {target}{(t.HasFlag(CycleModes.Off) ? " with Off allowed" : "")}; nothing was sent.");
        if (allowOff == SettingOutcome.Sent || cycle == SettingOutcome.Sent)
            return new(OffCycleStatus.Sent, before, plan.Target, allowOff, cycle,
                $"{(t.HasFlag(CycleModes.Off) ? SentMessage : SentMessageNoOff)} (Requested cycle: {target}.)");
        return new(OffCycleStatus.Confirmed, before, plan.Target, allowOff, cycle,
            $"The AirPods confirmed the press-and-hold cycle {target}{(t.HasFlag(CycleModes.Off) ? " (Off allowed)" : "")}.");
    }

    /// <summary>The three facts plus the hold action, one line each (UI and CLI).</summary>
    public IReadOnlyList<string> StatusLines()
    {
        static string W(SettingOutcome o) => o switch
        {
            SettingOutcome.AlreadySet => "already set / not needed (not sent)",
            SettingOutcome.NotSent => "not sent",
            SettingOutcome.SendFailed => "send failed",
            SettingOutcome.Sent => "sent, no answer",
            SettingOutcome.Confirmed => "confirmed by the AirPods",
            SettingOutcome.DeviceReportedDifferent => "AirPods answered a different value",
            SettingOutcome.Unknown => "unknown (no answer from the bridge)",
            _ => o.ToString(),
        };
        return
        [
            $"Allow Off (0x34): {W(AllowOff)}",
            $"Press-and-hold cycle (0x1A){(Target is { } t ? " " + CycleModeSet.Describe(t) : "")}: {W(Cycle)}",
            "Commands sent: " + (AnythingSent ? "yes" : "no"),
            "Confirmed by the AirPods: " + (Status == OffCycleStatus.Confirmed ? "yes" : Status == OffCycleStatus.AlreadySet ? "reported as already set" :
                AnythingSent ? (AllowOff == SettingOutcome.Confirmed || Cycle == SettingOutcome.Confirmed ? "partly" : "no") : "—"),
            "Kept after reconnecting / case: not verified by the app — hold a stem to check",
            "Before: " + (Before.Cycle is { } bc ? "cycle " + CycleModeSet.Describe(bc) : "cycle not reported") +
                (Before.AllowOff is { } ao ? $", Off {(ao ? "allowed" : "not allowed")}" : ", Allow Off not reported"),
            HoldNote(Before.Hold),
        ];
    }

    /// <summary>The app never writes 0x16; explains what that means for reaching the cycle from each stem.</summary>
    public static string HoldNote(HoldAssignment? hold)
    {
        if (hold is null)
            return "Press-and-hold action of the stems: not changed (the AirPods did not report it). A stem set to Siri does not cycle modes.";
        var siri = new List<string>();
        if (hold.LeftAction != HoldAction.NoiseControl) siri.Add("left");
        if (hold.RightAction != HoldAction.NoiseControl) siri.Add("right");
        return siri.Count == 0
            ? $"Press-and-hold: noise control on both stems ({hold.Describe()}), not changed."
            : $"Press-and-hold ({hold.Describe()}): the {string.Join(" and ", siri)} stem does not cycle modes; this app did not change that.";
    }
}

/// <summary>What the app knows about the AirPods' cycle, kept apart from the user's desired set.</summary>
public static class HoldCycleStatus
{
    /// <param name="reported">Cycle the AirPods themselves reported (0x1A) in a session, with its time.</param>
    /// <param name="lastApplied">Last cycle this app wrote, its time and outcome.</param>
    public static string Describe((CycleModes modes, DateTimeOffset at)? reported, (CycleModes modes, DateTimeOffset at, OffCycleStatus status)? lastApplied)
    {
        static string T(DateTimeOffset t) => t.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
        // the newest fact the AirPods themselves gave wins
        if (lastApplied is { } la && la.status is OffCycleStatus.Confirmed or OffCycleStatus.AlreadySet && (reported is null || la.at >= reported.Value.at))
            return $"AirPods confirmed: {CycleModeSet.Describe(la.modes)} ({T(la.at)}). Kept after reconnecting: not verified.";
        if (reported is { } r)
            return $"AirPods reported: {CycleModeSet.Describe(r.modes)} ({T(r.at)})." +
                   (lastApplied is { } l2 && l2.at > r.at ? $" Sent later by this app: {CycleModeSet.Describe(l2.modes)} ({T(l2.at)}), not confirmed by the AirPods." : "");
        return "Current AirPods set: unknown." +
               (lastApplied is { } l && l.status is OffCycleStatus.Sent or OffCycleStatus.Failed or OffCycleStatus.Refused
                   ? $" Last sent by this app: {CycleModeSet.Describe(l.modes)} ({T(l.at)}, {(l.status == OffCycleStatus.Sent ? "no answer from the AirPods" : l.status.ToString().ToLowerInvariant())})."
                   : "");
    }
}
