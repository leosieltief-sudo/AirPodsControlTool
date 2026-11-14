using AirPodsControl.Core.Bridge;
using AirPodsControl.Core.Protocol;
using AirPodsControl.Core.Services;
using Xunit;

namespace AirPodsControl.Tests;

/// <summary>Scripted Windows side of a hand-over: records every call in order.</summary>
internal sealed class FakeHost : IHandoverHost
{
    public readonly List<string> Calls = [];
    public AdapterResolution Adapter = new(new AdapterTarget("2-3", @"USB\VID_13D3&PID_3622\000000000", @"\\?\USB#X", true, "test adapter"), null);
    public InputGuardReport Input = InputGuardPolicy.Evaluate([], []);
    public bool ConfirmAnswer;
    public string? AttachError;
    public bool LockHeldElsewhere;
    public TaskCompletionSource? AttachGate;   // lets a test hold the operation inside "attach"

    private void Log(string s) { lock (Calls) Calls.Add(s); }

    public IDisposable? TryAcquireSystemLock(OperationTrace op, out string? holder)
    {
        holder = LockHeldElsewhere ? "airpodsctl pid 42" : null;
        Log("lock");
        return LockHeldElsewhere ? null : new Releaser(() => Log("unlock"));
    }

    public Task<AdapterResolution> ResolveAdapterAsync(OperationTrace op, CancellationToken ct) { Log("resolve"); return Task.FromResult(Adapter); }
    public Task<InputGuardReport> CheckInputDevicesAsync(AdapterTarget t, ulong? ex, OperationTrace op, CancellationToken ct) { Log("input"); return Task.FromResult(Input); }
    public Task<bool> ConfirmInputLossAsync(InputGuardReport r, OperationTrace op) { Log("confirm"); return Task.FromResult(ConfirmAnswer); }
    public Task<(bool turnedOff, string? error)> RadioOffAsync(AdapterTarget t, OperationTrace op) { Log("radio_off"); return Task.FromResult((true, (string?)null)); }

    public async Task<string?> AttachAsync(AdapterTarget t, OperationTrace op, CancellationToken ct)
    {
        Log("attach");
        if (AttachGate is not null) await AttachGate.Task.WaitAsync(ct);
        return AttachError;
    }

    public Task DetachAsync(AdapterTarget t, OperationTrace op) { Log("detach"); return Task.CompletedTask; }
    public Task RadioOnAsync(AdapterTarget t, OperationTrace op) { Log("radio_on"); return Task.CompletedTask; }
    public void OperationStarted(OperationTrace op, ulong? device) => Log("probe_start");
    public void OperationFinished(OperationTrace op) => Log("probe_finish");

    private sealed class Releaser(Action a) : IDisposable { public void Dispose() => a(); }
}

public class HandoverCoordinatorTests
{
    private static readonly HandoverRequest Switch = new(BridgeOperationKind.SwitchMode, "hotkey", 0xA1B2C3D4E5F6);

    private static ModeSwitchRequest Req(ListeningMode m, string by = "hotkey") => new(0xA1B2C3D4E5F6, "AirPods", null, m, true, by);

    private static Task<HandoverResult<string>> Run(HandoverCoordinator c, Func<HandoverSession, Task<string>>? op = null, CancellationToken ct = default) =>
        c.RunAsync(Switch, null, (s, _) => (op ?? (_ => Task.FromResult("done")))(s), s => { return Task.CompletedTask; }, ct);

    [Fact]
    public void One_operation_is_exactly_one_hand_over_and_back()
    {
        var host = new FakeHost();
        var r = Run(new HandoverCoordinator(host)).Result;
        Assert.True(r.Completed);
        Assert.True(r.Disrupted);
        Assert.Equal(["lock", "probe_start", "resolve", "input", "radio_off", "attach", "detach", "radio_on", "probe_finish", "unlock"], host.Calls);
    }

    [Fact]
    public void Connected_mouse_blocks_before_anything_is_switched_off()
    {
        var host = new FakeHost
        {
            Input = InputGuardPolicy.Evaluate([new InputDeviceInfo("MX Master", 1, InputTransport.LowEnergy, "test", true, true)], []),
            ConfirmAnswer = false,
        };
        var r = Run(new HandoverCoordinator(host)).Result;
        Assert.Equal(HandoverStage.InputGuard, r.Stage);
        Assert.False(r.Disrupted);
        Assert.Contains("MX Master", r.Error);
        Assert.DoesNotContain("radio_off", host.Calls);
        Assert.DoesNotContain("attach", host.Calls);
        Assert.Contains("confirm", host.Calls);
        Assert.True(host.Calls.IndexOf("confirm") < host.Calls.Count);
    }

    [Fact]
    public void Explicit_user_decision_overrides_the_guard()
    {
        var host = new FakeHost
        {
            Input = InputGuardPolicy.Evaluate([new InputDeviceInfo("Keyboard", 2, InputTransport.Classic, "test", true, true)], []),
            ConfirmAnswer = true,
        };
        var r = Run(new HandoverCoordinator(host)).Result;
        Assert.True(r.Completed);
        Assert.True(host.Calls.IndexOf("confirm") < host.Calls.IndexOf("radio_off"));
    }

    [Fact]
    public void Pre_approval_flag_skips_the_dialog_but_unknown_without_it_blocks()
    {
        var unknown = InputGuardPolicy.Evaluate([], ["device tree not readable"]);
        var host = new FakeHost { Input = unknown, ConfirmAnswer = false };
        var blocked = Run(new HandoverCoordinator(host)).Result;
        Assert.Equal(HandoverStage.InputGuard, blocked.Stage);
        Assert.Contains("Could not determine", blocked.Error);

        var host2 = new FakeHost { Input = unknown, ConfirmAnswer = false };
        var c2 = new HandoverCoordinator(host2);
        var r2 = c2.RunAsync(Switch with { InputLossApproved = true }, null, (s, _) => Task.FromResult("ok"), null, default).Result;
        Assert.True(r2.Completed);
        Assert.DoesNotContain("confirm", host2.Calls);
    }

    [Fact]
    public void Ambiguous_adapter_stops_before_the_radio()
    {
        var host = new FakeHost { Adapter = AdapterResolution.Fail("2 adapters 13D3:3622 are connected and it is unclear which one to hand over") };
        var r = Run(new HandoverCoordinator(host)).Result;
        Assert.Equal(HandoverStage.Adapter, r.Stage);
        Assert.DoesNotContain("input", host.Calls);
        Assert.DoesNotContain("radio_off", host.Calls);
    }

    [Fact]
    public void Second_operation_while_one_runs_is_rejected_without_side_effects()
    {
        var host = new FakeHost { AttachGate = new TaskCompletionSource() };
        var c = new HandoverCoordinator(host);
        var first = Run(c);
        SpinWait.SpinUntil(() => c.IsBusy && host.Calls.Contains("attach"), 2000);
        int callsBefore;
        lock (host.Calls) callsBefore = host.Calls.Count;
        var second = Run(c).Result;
        Assert.Equal(HandoverStage.Busy, second.Stage);
        Assert.False(second.Disrupted);
        lock (host.Calls) Assert.Equal(callsBefore, host.Calls.Count);   // nothing touched
        host.AttachGate.SetResult();
        Assert.True(first.Result.Completed);
        Assert.Single(host.Calls, "radio_off");
        Assert.Single(host.Calls, "detach");
    }

    [Fact]
    public void Lock_held_by_another_process_rejects()
    {
        var host = new FakeHost { LockHeldElsewhere = true };
        var r = Run(new HandoverCoordinator(host)).Result;
        Assert.Equal(HandoverStage.Lock, r.Stage);
        Assert.Contains("airpodsctl", r.Error);
        Assert.Equal(["lock"], host.Calls);
    }

    [Fact]
    public void Failure_and_cancellation_still_return_adapter_and_radio()
    {
        var host = new FakeHost();
        var r = Run(new HandoverCoordinator(host), _ => throw new IOException("daemon pipe broke")).Result;
        Assert.Equal(HandoverStage.Operation, r.Stage);
        Assert.True(r.Disrupted);
        Assert.Contains("detach", host.Calls);
        Assert.Contains("radio_on", host.Calls);

        var host2 = new FakeHost { AttachGate = new TaskCompletionSource() };
        using var cts = new CancellationTokenSource();
        var t = Run(new HandoverCoordinator(host2), ct: cts.Token);
        SpinWait.SpinUntil(() => host2.Calls.Contains("attach"), 2000);
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => t.GetAwaiter().GetResult());
        Assert.Contains("radio_on", host2.Calls);         // radio was off: switched back on
        Assert.DoesNotContain("detach", host2.Calls);     // attach never completed: nothing to detach
    }

    [Fact]
    public void Attach_failure_turns_the_radio_back_on()
    {
        var host = new FakeHost { AttachError = "usbipd attach failed: busy" };
        var r = Run(new HandoverCoordinator(host)).Result;
        Assert.Equal(HandoverStage.Attach, r.Stage);
        Assert.True(r.Disrupted);
        Assert.Contains("radio_on", host.Calls);
        Assert.DoesNotContain("detach", host.Calls);
    }

    [Fact]
    public void Presses_during_a_switch_merge_latest_wins_and_repeats_are_ignored()
    {
        var host = new FakeHost();
        var c = new HandoverCoordinator(host);
        var sent = new List<ListeningMode>();
        Assert.Equal(MergeResult.NotAccepting, c.TryMerge(Req(ListeningMode.Off)));   // nothing running

        var r = Run(c, async s =>
        {
            ModeSwitchRequest? cur = Req(ListeningMode.Transparency);
            while (cur is not null)
            {
                s.Sending(cur.Mode);
                sent.Add(cur.Mode);
                if (sent.Count == 1)
                {
                    Assert.Equal(MergeResult.DuplicateIgnored, c.TryMerge(Req(ListeningMode.Transparency)));   // repeat press
                    Assert.Equal(MergeResult.Merged, c.TryMerge(Req(ListeningMode.Off)));
                    Assert.Equal(MergeResult.Merged, c.TryMerge(Req(ListeningMode.NoiseCancellation)));         // latest wins
                    Assert.Equal(MergeResult.DuplicateIgnored, c.TryMerge(Req(ListeningMode.NoiseCancellation)));
                }
                await Task.Yield();
                cur = s.NextOrClose();
            }
            Assert.Equal(MergeResult.NotAccepting, c.TryMerge(Req(ListeningMode.Off)));   // adapter on its way back
            return "ok";
        }).Result;
        Assert.True(r.Completed);
        Assert.Equal([ListeningMode.Transparency, ListeningMode.NoiseCancellation], sent);
        Assert.Single(host.Calls, "attach");
        Assert.Equal(MergeResult.NotAccepting, c.TryMerge(Req(ListeningMode.Off)));
    }

    [Fact]
    public void Pressing_the_mode_in_flight_cancels_a_pending_change()
    {
        var host = new FakeHost();
        var c = new HandoverCoordinator(host);
        var sent = new List<ListeningMode>();
        Run(c, s =>
        {
            s.Sending(ListeningMode.Transparency); sent.Add(ListeningMode.Transparency);
            c.TryMerge(Req(ListeningMode.Off));
            Assert.Equal(MergeResult.Merged, c.TryMerge(Req(ListeningMode.Transparency)));   // back to what is being sent
            while (s.NextOrClose() is { } n) sent.Add(n.Mode);
            return Task.FromResult("ok");
        }).Wait();
        Assert.Equal([ListeningMode.Transparency], sent);
    }

    [Fact]
    public void Battery_reads_do_not_accept_merges()
    {
        var host = new FakeHost();
        var c = new HandoverCoordinator(host);
        MergeResult during = MergeResult.Merged;
        c.RunAsync(Switch with { Kind = BridgeOperationKind.ReadBattery }, null, (s, _) =>
        {
            during = c.TryMerge(Req(ListeningMode.Off));
            return Task.FromResult("ok");
        }, null, default).Wait();
        Assert.Equal(MergeResult.NotAccepting, during);
    }

    [Fact]
    public void Recovery_does_not_run_during_an_operation()
    {
        var host = new FakeHost { AttachGate = new TaskCompletionSource() };
        var c = new HandoverCoordinator(host);
        var op = Run(c);
        SpinWait.SpinUntil(() => host.Calls.Contains("attach"), 2000);
        bool ran = false;
        Assert.False(c.TryRunExclusiveAsync(BridgeOperationKind.Recovery, "startup", _ => { ran = true; return Task.CompletedTask; }).Result);
        Assert.False(ran);
        host.AttachGate.SetResult();
        op.Wait();
        Assert.True(c.TryRunExclusiveAsync(BridgeOperationKind.Recovery, "startup", _ => { ran = true; return Task.CompletedTask; }).Result);
        Assert.True(ran);
    }

    [Fact]
    public void Trace_records_initiator_and_steps_in_order()
    {
        var events = new List<string>();
        var c = new HandoverCoordinator(new FakeHost(), (op, e) => { lock (events) events.Add(e.Name); });
        var r = Run(c).Result;
        Assert.Equal("hotkey", r.Op.Initiator);
        Assert.StartsWith("op", r.Op.Id);
        Assert.Equal("begin", events[0]);
        Assert.True(events.IndexOf("input_guard") < events.IndexOf("end"));
        Assert.Equal("end", events[^1]);
    }
}

public class InputGuardTests
{
    private static InputDeviceInfo D(bool? connected, bool? onTarget, InputTransport t = InputTransport.Classic) =>
        new("dev", 1, t, "test", connected, onTarget);

    [Fact]
    public void Paired_but_disconnected_devices_do_not_block()
    {
        Assert.Equal(InputGuardStatus.Clear, InputGuardPolicy.Evaluate([D(false, true), D(false, true, InputTransport.LowEnergy)], []).Status);
    }

    [Fact]
    public void Connected_device_on_this_adapter_blocks()
    {
        var r = InputGuardPolicy.Evaluate([D(true, true, InputTransport.LowEnergy)], []);
        Assert.Equal(InputGuardStatus.InputDevicesConnected, r.Status);
        Assert.False(InputGuardPolicy.MayProceed(r, explicitUserOverride: false));
        Assert.True(InputGuardPolicy.MayProceed(r, explicitUserOverride: true));
        Assert.Contains("BLE HID", r.Explain());
    }

    [Fact]
    public void Device_on_another_adapter_does_not_block()
    {
        Assert.Equal(InputGuardStatus.Clear, InputGuardPolicy.Evaluate([D(true, false)], []).Status);
    }

    [Fact]
    public void Undetermined_state_or_ownership_is_unknown_not_safe()
    {
        Assert.Equal(InputGuardStatus.Unknown, InputGuardPolicy.Evaluate([D(null, true)], []).Status);
        Assert.Equal(InputGuardStatus.Unknown, InputGuardPolicy.Evaluate([D(true, null)], []).Status);
        var r = InputGuardPolicy.Evaluate([], ["scan failed"]);
        Assert.Equal(InputGuardStatus.Unknown, r.Status);
        Assert.False(InputGuardPolicy.MayProceed(r, false));
        Assert.Contains("not known to be safe", r.Explain());
    }
}

public class AdapterMatcherTests
{
    private static UsbipdDevice U(string bus, string inst, bool shared = true) =>
        new(bus, UsbipdParser.VidPidFromInstanceId(inst), inst, "MediaTek Bluetooth Adapter", shared, false);

    private const string A = @"USB\VID_13D3&PID_3622\000000000";
    private const string B = @"USB\VID_13D3&PID_3622\000000001";
    private static WindowsRadioInfo RadioOf(string usb) =>
        new(@"\\?\USB#VID_13D3&PID_3622&MI_00#7&3b2f8856&0&0000#{92383b0e-f90e-4ac9-8d44-8c2d0d0ebda2}",
            [@"USB\VID_13D3&PID_3622&MI_00\7&3b2f8856&0&0000", usb, @"USB\ROOT_HUB30\4&1"]);

    [Fact]
    public void Interface_path_converts_to_instance_id()
    {
        Assert.Equal(@"USB\VID_13D3&PID_3622&MI_00\7&3b2f8856&0&0000",
            AdapterMatcher.InterfacePathToInstanceId(@"\\?\USB#VID_13D3&PID_3622&MI_00#7&3b2f8856&0&0000#{92383b0e-f90e-4ac9-8d44-8c2d0d0ebda2}"));
    }

    [Fact]
    public void Composite_radio_interface_maps_to_its_usb_parent()
    {
        var r = AdapterMatcher.Resolve("13D3:3622", null, [U("2-3", A)], RadioOf(A));
        Assert.True(r.Ok, r.Error);
        Assert.True(r.Target!.RadioBelongsToAdapter);
        Assert.Equal("2-3", r.Target.BusId);
    }

    [Fact]
    public void Two_identical_adapters_without_a_pinned_instance_use_only_the_radio_link()
    {
        var r = AdapterMatcher.Resolve("13D3:3622", null, [U("2-3", A), U("2-4", B)], RadioOf(B));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("2-4", r.Target!.BusId);

        var noRadio = AdapterMatcher.Resolve("13D3:3622", null, [U("2-3", A), U("2-4", B)], null);
        Assert.False(noRadio.Ok);
        Assert.Contains("unclear", noRadio.Error);
    }

    [Fact]
    public void Pinned_instance_wins_and_a_different_windows_radio_is_left_alone()
    {
        var r = AdapterMatcher.Resolve("13D3:3622", A, [U("2-3", A), U("2-4", B)], RadioOf(B));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("2-3", r.Target!.BusId);
        Assert.False(r.Target.RadioBelongsToAdapter);    // Windows uses the other adapter: its radio is not touched
        Assert.Null(r.Target.WindowsRadioDeviceId);
    }

    [Fact]
    public void Missing_pinned_device_unshared_or_unmapped_radio_fail()
    {
        Assert.False(AdapterMatcher.Resolve("13D3:3622", B, [U("2-3", A)], RadioOf(A)).Ok);
        Assert.Contains("not shared", AdapterMatcher.Resolve("13D3:3622", null, [U("2-3", A, shared: false)], RadioOf(A)).Error);
        Assert.Contains("Could not map", AdapterMatcher.Resolve("13D3:3622", null, [U("2-3", A)], new WindowsRadioInfo(@"\\?\X", [])).Error);
    }
}

public class ResultSemanticsTests
{
    [Fact]
    public void Sent_confirmed_and_failed_are_distinct()
    {
        NoiseControlResult Map(string json) => BridgeResultParser.ToNoiseControlResult(BridgeResultParser.Parse("RESULT " + json), ListeningMode.Transparency);
        var sent = Map("""{"ok": true, "outcome": "sent", "handshake_ok": true}""");
        var confirmed = Map("""{"ok": true, "outcome": "confirmed", "reported": 3}""");
        var noAnswer = Map("""{"ok": false, "outcome": "no_response"}""");
        var failed = Map("""{"ok": false, "outcome": "transport_error", "error": "EPIPE"}""");
        Assert.Equal(NoiseControlOutcome.Sent, sent.Outcome);
        Assert.True(sent.Success); Assert.False(sent.Confirmed);
        Assert.Contains("do not confirm", sent.Message);
        Assert.Equal(NoiseControlOutcome.Confirmed, confirmed.Outcome);
        Assert.True(confirmed.Confirmed);
        Assert.Equal(NoiseControlOutcome.NoResponse, noAnswer.Outcome);
        Assert.False(noAnswer.Success);
        Assert.Equal(NoiseControlOutcome.TransportError, failed.Outcome);
        Assert.False(failed.Success);
    }

    [Fact]
    public void A_handshake_alone_is_not_a_confirmation()
    {
        // the parser must not invent an echo: no "reported" mode → never Confirmed
        var r = BridgeResultParser.ToNoiseControlResult(BridgeResultParser.Parse("""RESULT {"ok": true, "outcome": "sent", "handshake_ok": true, "rx_packets": 9}"""), ListeningMode.Off);
        Assert.False(r.Confirmed);
        Assert.Null(r.Reported);
    }
}
