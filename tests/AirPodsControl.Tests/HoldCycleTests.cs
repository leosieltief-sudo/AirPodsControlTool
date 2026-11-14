using AirPodsControl.Core.Bridge;
using AirPodsControl.Core.Models;
using AirPodsControl.Core.Protocol;
using AirPodsControl.Core.Services;
using Xunit;

namespace AirPodsControl.Tests;

/// <summary>Press-and-hold cycle (Settings → Apply to AirPods, CLI bridge-hold-cycle / bridge-enable-off): packets (0x34, 0x1A),
/// cycle validation, planning from what the AirPods reported, the three-way result (sent / confirmed / persistence not
/// verified) and the status line that keeps the desired set apart from the AirPods' state.</summary>
public class HoldCycleTests
{
    private static readonly AirPodsModelInfo Pro3 = AirPodsCatalog.ByProductId(0x2027)!;
    private static readonly AirPodsModelInfo Pro1 = AirPodsCatalog.ByProductId(0x200E)!;
    private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));
    private const CycleModes O = CycleModes.Off, N = CycleModes.NoiseCancellation, T = CycleModes.Transparency, A = CycleModes.Adaptive;

    // ---------------------------------------------------------------- packets

    [Fact]
    public void Cycle_packet_is_control_command_0x1A_with_the_mask()
    {
        Assert.Equal(Hex("04 00 04 00 09 00 1A 07 00 00 00"), AapPackets.SetListeningModeCycle((byte)(O | N | T)));
        // LibrePods "Configure Stem Long Press": 1A 0B = Off + ANC + Adaptive
        Assert.Equal(Hex("04 00 04 00 09 00 1A 0B 00 00 00"), AapPackets.SetListeningModeCycle((byte)(O | N | A)));
        var p = AapPackets.SetListeningModeCycle(0x0F);
        Assert.Equal(AapOpcode.ControlCommand, (ushort)(p[4] | p[5] << 8));   // opcode 0x0009 …
        Assert.Equal(ControlId.ListeningModeConfigs, p[6]);                    // … control id 0x1A: not the same thing
    }

    [Fact]
    public void Allow_off_packet_is_control_0x34_value_01()
    {
        Assert.Equal(Hex("04 00 04 00 09 00 34 01 00 00 00"), AapPackets.SetAllowOffOption(true));
    }

    [Fact]
    public void Cycle_bits_are_not_listening_mode_values()
    {
        // 0x1A bitmask vs 0x0D values: Transparency is 0x04 in the mask but 0x03 as a mode, Adaptive 0x08 vs 0x04
        Assert.Equal(0x04, (byte)T);
        Assert.Equal(0x03, (byte)ListeningMode.Transparency);
        Assert.Equal(0x08, (byte)A);
        Assert.Equal(0x04, (byte)ListeningMode.Adaptive);
    }

    // ---------------------------------------------------------------- set parsing / validation

    [Fact]
    public void Parses_cli_lists()
    {
        Assert.Equal(N | T, CycleModeSet.Parse("anc,transparency", out _));
        Assert.Equal(O | N | A, CycleModeSet.Parse(" OFF , anc ,Adaptive", out _));
        Assert.Null(CycleModeSet.Parse("anc,loud", out var e1)); Assert.Contains("loud", e1);
        Assert.Null(CycleModeSet.Parse("", out var e2)); Assert.NotNull(e2);
    }

    [Theory]
    [InlineData((byte)0x03, true)]   // Off + ANC
    [InlineData((byte)0x0F, true)]
    [InlineData((byte)0x01, false)]  // Off alone: nothing to cycle
    [InlineData((byte)0x00, false)]
    [InlineData((byte)0x11, false)]  // undocumented bit
    public void Validates_masks(byte mask, bool ok) =>
        Assert.Equal(ok, CycleModeSet.Validate((CycleModes)mask, Pro3) is null);

    [Fact]
    public void Adaptive_only_on_models_that_have_it()
    {
        Assert.Null(CycleModeSet.Validate(O | A, Pro3));
        Assert.Contains("Adaptive", CycleModeSet.Validate(O | A, Pro1));
    }

    [Fact]
    public void Request_validation_uses_the_exact_set_and_the_model()
    {
        Assert.Null(OffCyclePlanner.ValidateRequest(new OffCycleRequest(1, O | N), Pro3));
        Assert.Null(OffCyclePlanner.ValidateRequest(new OffCycleRequest(1, N | T), Pro3));      // without Off is fine too
        Assert.NotNull(OffCyclePlanner.ValidateRequest(new OffCycleRequest(1, N), Pro3));       // one mode: nothing to switch between
        Assert.NotNull(OffCyclePlanner.ValidateRequest(new OffCycleRequest(1, CycleModes.None), Pro3));
        Assert.Null(OffCyclePlanner.ValidateRequest(new OffCycleRequest(1, null), Pro3));        // keep-current: decided in the session
        var airpods2 = AirPodsCatalog.ByProductId(0x200F)!;
        Assert.NotNull(OffCyclePlanner.ValidateRequest(new OffCycleRequest(1, O | N), airpods2));
        Assert.Contains("Off", CycleModeSet.Validate(O | N, AirPodsCatalog.ByProductId(0x201B)! with { OffMode = OffModeRequirement.NotApplicable }));
    }

    // ---------------------------------------------------------------- planning

    [Fact]
    public void Off_in_the_set_also_sends_allow_off()
    {
        var p = OffCyclePlanner.Plan(new OffCycleRequest(1, O | N | T), OffCycleState.Unknown, Pro3);
        Assert.True(p.Proceed);
        Assert.True(p.SendAllowOff);
        Assert.Equal(O | N | T, p.SendCycle);
        Assert.False(p.TargetFromDevice);
    }

    [Fact]
    public void Set_without_off_writes_only_the_cycle_and_leaves_allow_off_alone()
    {
        var p = OffCyclePlanner.Plan(new OffCycleRequest(1, N | T | A), OffCycleState.Unknown, Pro3);
        Assert.False(p.SendAllowOff);                 // never sends 0x34 = 02 either
        Assert.Equal(N | T | A, p.SendCycle);
        var r = OffCycleResult.Build(p, OffCycleState.Unknown, SettingOutcome.AlreadySet, SettingOutcome.Sent);
        Assert.Equal(OffCycleStatus.Sent, r.Status);
        Assert.StartsWith(OffCycleResult.SentMessageNoOff, r.Message);
    }

    [Fact]
    public void Keep_current_preserves_the_reported_modes_and_adds_off()
    {
        var state = new OffCycleState(false, N | T | A, null);
        var p = OffCyclePlanner.Plan(new OffCycleRequest(1, null), state, Pro3);
        Assert.Equal(O | N | T | A, p.SendCycle);
        Assert.True(p.TargetFromDevice);
        Assert.True(p.SendAllowOff);
    }

    [Fact]
    public void Keep_current_with_unreported_cycle_writes_nothing()
    {
        // e.g. AirPods Pro 3 on the bridge without audio: no 0x09 packets at all — never fall back to a guessed set
        var p = OffCyclePlanner.Plan(new OffCycleRequest(1, null), new OffCycleState(true, null, null), Pro3);
        Assert.False(p.Proceed);
        Assert.True(p.NothingToSend);
        Assert.Null(p.SendCycle);
        Assert.Equal(OffCyclePlanner.CurrentUnknownMessage, p.StopReason);
        var r = OffCycleResult.Build(p, OffCycleState.Unknown, SettingOutcome.NotSent, SettingOutcome.NotSent);
        Assert.Equal(OffCycleStatus.NothingSent, r.Status);
        Assert.False(r.AnythingSent);
        Assert.False(r.Success);
        Assert.Contains("explicitly", r.Message);
    }

    [Fact]
    public void Values_already_reported_are_not_written_again()
    {
        var p = OffCyclePlanner.Plan(new OffCycleRequest(1, null), new OffCycleState(true, O | N | T, null), Pro3);
        Assert.True(p.Proceed);
        Assert.True(p.NothingToSend);
        var r = OffCycleResult.Build(p, new OffCycleState(true, O | N | T, null), SettingOutcome.AlreadySet, SettingOutcome.AlreadySet);
        Assert.Equal(OffCycleStatus.AlreadySet, r.Status);
        Assert.False(r.AnythingSent);

        var onlyCycle = OffCyclePlanner.Plan(new OffCycleRequest(1, O | N | T), new OffCycleState(true, N | T, null), Pro3);
        Assert.False(onlyCycle.SendAllowOff);         // reported as allowed already
        Assert.Equal(O | N | T, onlyCycle.SendCycle);
    }

    [Fact]
    public void Explicit_choice_wins_over_the_reported_cycle()
    {
        var p = OffCyclePlanner.Plan(new OffCycleRequest(1, O | N), new OffCycleState(null, N | T | A, null), Pro3);
        Assert.Equal(O | N, p.SendCycle);
    }

    [Fact]
    public void Reported_cycle_that_becomes_invalid_with_off_is_not_written()
    {
        var p = OffCyclePlanner.Plan(new OffCycleRequest(1, null), new OffCycleState(null, N | A, null), Pro1);   // Pro 1: no Adaptive
        Assert.False(p.Proceed);
        Assert.Contains("Adaptive", p.StopReason);
    }

    // ---------------------------------------------------------------- results

    private static OffCyclePlan Plan(CycleModes modes = O | N | T) => OffCyclePlanner.Plan(new OffCycleRequest(1, modes), OffCycleState.Unknown, Pro3);

    [Fact]
    public void No_answer_is_sent_not_confirmed_and_never_claims_persistence()
    {
        var r = OffCycleResult.Build(Plan(), OffCycleState.Unknown, SettingOutcome.Sent, SettingOutcome.Sent);
        Assert.Equal(OffCycleStatus.Sent, r.Status);
        Assert.StartsWith(OffCycleResult.SentMessage, r.Message);
        Assert.True(r.Success);
        Assert.True(r.AnythingSent);
        Assert.False(r.PersistenceVerified);
        var lines = r.StatusLines();
        Assert.Contains("Commands sent: yes", lines);
        Assert.Contains("Confirmed by the AirPods: no", lines);
        Assert.Contains(lines, l => l.StartsWith("Kept after reconnecting") && l.Contains("not verified"));
        Assert.DoesNotContain("permanent", r.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Both_answers_are_confirmed_but_persistence_still_unverified()
    {
        var r = OffCycleResult.Build(Plan(), OffCycleState.Unknown, SettingOutcome.Confirmed, SettingOutcome.Confirmed, 0x01, 0x07);
        Assert.Equal(OffCycleStatus.Confirmed, r.Status);
        Assert.Contains("Confirmed by the AirPods: yes", r.StatusLines());
        Assert.False(r.PersistenceVerified);
    }

    [Fact]
    public void One_answer_is_partly_confirmed()
    {
        var r = OffCycleResult.Build(Plan(), OffCycleState.Unknown, SettingOutcome.Confirmed, SettingOutcome.Sent);
        Assert.Equal(OffCycleStatus.Sent, r.Status);
        Assert.Contains("Confirmed by the AirPods: partly", r.StatusLines());
    }

    [Fact]
    public void Refused_allow_off_and_different_cycle_are_reported()
    {
        var a = OffCycleResult.Build(Plan(), OffCycleState.Unknown, SettingOutcome.DeviceReportedDifferent, SettingOutcome.NotSent, allowOffEcho: 0x02);
        Assert.Equal(OffCycleStatus.Refused, a.Status);
        Assert.Contains("0x02", a.Message);
        Assert.False(a.Success);
        var c = OffCycleResult.Build(Plan(), OffCycleState.Unknown, SettingOutcome.Confirmed, SettingOutcome.DeviceReportedDifferent, cycleEcho: 0x06);
        Assert.Equal(OffCycleStatus.Refused, c.Status);
        Assert.Contains("Noise Cancellation, Transparency", c.Message);
    }

    [Fact]
    public void Send_failure_and_lost_bridge_are_failures_without_retry()
    {
        var f = OffCycleResult.Build(Plan(), OffCycleState.Unknown, SettingOutcome.SendFailed, SettingOutcome.NotSent, error: "EPIPE");
        Assert.Equal(OffCycleStatus.Failed, f.Status);
        Assert.Contains("Nothing was retried", f.Message);
        Assert.False(f.AnythingSent);
        var u = OffCycleResult.Build(Plan(), OffCycleState.Unknown, SettingOutcome.Unknown, SettingOutcome.Unknown);
        Assert.Equal(OffCycleStatus.Failed, u.Status);
        Assert.True(u.AnythingSent);     // may have been written: not reported as "nothing sent"
        Assert.Contains("unknown", u.Message);
    }

    [Fact]
    public void Hold_action_notes_never_hide_siri()
    {
        Assert.Contains("not changed", OffCycleResult.HoldNote(null));
        var siriLeft = OffCycleResult.HoldNote(new HoldAssignment(Right: 0x01, Left: 0x05));
        Assert.Contains("left stem does not cycle", siriLeft);
        Assert.Contains("did not change", siriLeft);
        Assert.Contains("both stems", OffCycleResult.HoldNote(new HoldAssignment(0x01, 0x01)));
    }

    // ---------------------------------------------------------------- daemon replies

    [Fact]
    public void State_comes_only_from_reported_controls()
    {
        var s = OffCycleReplies.ParseState("""{"ok": true, "controls": {"1a": [14, 0], "34": [1, 0], "16": [1, 5]}}""");
        Assert.Equal(N | T | A, s.Cycle);
        Assert.True(s.AllowOff);
        Assert.Equal(new HoldAssignment(0x01, 0x05), s.Hold);

        var none = OffCycleReplies.ParseState("""{"ok": true, "controls": {}}""");
        Assert.Null(none.Cycle);      // not reported → unknown, never 0
        Assert.Null(none.AllowOff);
        Assert.Null(none.Hold);
        Assert.Equal(OffCycleState.Unknown, OffCycleReplies.ParseState("""{"ok": true}"""));
        Assert.Null(OffCycleReplies.ParseState("""{"controls": {"1a": [48, 0]}}""").Cycle);   // undocumented bits
    }

    [Fact]
    public void Settings_reply_maps_outcomes()
    {
        var (a, c) = OffCycleReplies.ParseSettings("""
            {"outcome": "configured", "settings": {"allow_off": {"outcome": "confirmed", "echo": 1}, "cycle": {"outcome": "sent", "echo": null}}}
            """);
        Assert.Equal(SettingOutcome.Confirmed, a!.Outcome);
        Assert.Equal((byte)1, a.Echo);
        Assert.Equal(SettingOutcome.Sent, c!.Outcome);
        Assert.Null(c.Echo);
        var (a2, c2) = OffCycleReplies.ParseSettings("""{"settings": {"cycle": {"outcome": "send_failed", "error": "EPIPE"}}}""");
        Assert.Null(a2);
        Assert.Equal(SettingOutcome.SendFailed, c2!.Outcome);
        Assert.Equal("EPIPE", c2.Error);
    }

    // ---------------------------------------------------------------- desired set vs AirPods state

    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Unknown_airpods_set_is_said_plainly()
    {
        Assert.Equal("Current AirPods set: unknown.", HoldCycleStatus.Describe(null, null));
        var sent = HoldCycleStatus.Describe(null, (O | N | T, T0, OffCycleStatus.Sent));
        Assert.StartsWith("Current AirPods set: unknown.", sent);       // a write without an answer is not the AirPods' state
        Assert.Contains("Last sent by this app: Off, Noise Cancellation, Transparency", sent);
        Assert.Contains("no answer", sent);
    }

    [Fact]
    public void Confirmed_and_reported_sets_are_labelled_as_such()
    {
        var c = HoldCycleStatus.Describe(null, (O | N, T0, OffCycleStatus.Confirmed));
        Assert.StartsWith("AirPods confirmed: Off, Noise Cancellation", c);
        Assert.Contains("Kept after reconnecting: not verified", c);
        var r = HoldCycleStatus.Describe((N | T, T0), null);
        Assert.StartsWith("AirPods reported: Noise Cancellation, Transparency", r);
        var later = HoldCycleStatus.Describe((N | T, T0), (O | N | T, T0.AddMinutes(5), OffCycleStatus.Sent));
        Assert.StartsWith("AirPods reported: Noise Cancellation, Transparency", later);
        Assert.Contains("not confirmed by the AirPods", later);
    }

    // ---------------------------------------------------------------- hand-over

    [Fact]
    public async Task Hold_cycle_hand_over_is_blocked_by_input_devices_before_radio_off_and_takes_no_merges()
    {
        var host = new FakeHost
        {
            Input = InputGuardPolicy.Evaluate([new InputDeviceInfo("MX Keys", 3, InputTransport.LowEnergy, "test", true, true)], []),
        };
        var coord = new HandoverCoordinator(host);
        var blocked = await coord.RunAsync(new HandoverRequest(BridgeOperationKind.HoldCycle, "test", 1), null,
            (s, c) => Task.FromResult(1), null, CancellationToken.None);
        Assert.Equal(HandoverStage.InputGuard, blocked.Stage);
        Assert.DoesNotContain("radio_off", host.Calls);

        host.Input = InputGuardPolicy.Evaluate([], []);
        MergeResult? during = null;
        var ok = await coord.RunAsync(new HandoverRequest(BridgeOperationKind.HoldCycle, "test", 1), null, (s, c) =>
        {
            during = coord.TryMerge(new ModeSwitchRequest(1, "AirPods", Pro3, ListeningMode.Off, true, "hotkey"));
            return Task.FromResult(1);
        }, null, CancellationToken.None);
        Assert.True(ok.Completed);
        Assert.Equal(MergeResult.NotAccepting, during);   // a hotkey press must not turn this into a mode switch
        Assert.Equal(1, host.Calls.Count(c => c == "attach"));
        Assert.Equal(1, host.Calls.Count(c => c == "detach"));
    }
}
