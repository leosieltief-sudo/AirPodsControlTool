using AirPodsControl.Core.Models;
using AirPodsControl.Core.Protocol;
using AirPodsControl.Core.Services;
using Xunit;

namespace AirPodsControl.Tests;

/// <summary>
/// In-memory transport used by integration tests. It does NOT pretend to be AirPods in production:
/// it only exists in the test assembly. Behaviour is scripted per test.
/// </summary>
internal sealed class ScriptedTransport : IAapTransport
{
    public readonly List<byte[]> Sent = new();
    public Func<byte[], byte[]?>? Responder;
    public string Name => "scripted";
    public bool IsOpen { get; private set; }
    public event Action<byte[]>? PacketReceived;
    public event Action<Exception?>? Closed;
    public Task OpenAsync(CancellationToken ct) { IsOpen = true; return Task.CompletedTask; }
    public Task SendAsync(byte[] packet, CancellationToken ct)
    {
        if (!IsOpen) throw new IOException("closed");
        Sent.Add(packet);
        var reply = Responder?.Invoke(packet);
        if (reply is not null) _ = Task.Run(async () => { await Task.Delay(20); PacketReceived?.Invoke(reply); });
        return Task.CompletedTask;
    }
    public void Inject(byte[] p) => PacketReceived?.Invoke(p);
    public void Drop() { IsOpen = false; Closed?.Invoke(new IOException("link lost")); }
    public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
}

public class NoiseControlTests
{
    static bool IsControl(byte[] p, byte id) => p.Length >= 8 && p[4] == 0x09 && p[6] == id;

    static async Task<(ScriptedTransport, AirPodsProtocolService, NoiseControlManager)> Setup(Func<byte[], byte[]?> responder, ushort pid = 0x2024)
    {
        var t = new ScriptedTransport { Responder = responder };
        var p = new AirPodsProtocolService(t) { InitPacketGap = TimeSpan.Zero };
        await p.StartAsync(CancellationToken.None);
        var m = new NoiseControlManager { Model = AirPodsCatalog.ByProductId(pid), ConfirmationTimeout = TimeSpan.FromMilliseconds(400) };
        m.Attach(p);
        return (t, p, m);
    }

    [Fact]
    public async Task Init_sequence_is_handshake_features_notifications()
    {
        var (t, _, _) = await Setup(_ => null);
        Assert.Equal(AapPackets.Handshake(), t.Sent[0]);
        Assert.Equal(AapPackets.SetFeatureFlags(), t.Sent[1]);
        Assert.Equal(AapPackets.RequestNotifications(), t.Sent[2]);
    }

    [Fact]
    public async Task Echo_confirms_mode()
    {
        var (_, _, m) = await Setup(p => IsControl(p, ControlId.ListeningMode) ? p : null);
        var r = await m.SetModeAsync(ListeningMode.NoiseCancellation);
        Assert.Equal(NoiseControlOutcome.Confirmed, r.Outcome);
        Assert.Equal(ListeningMode.NoiseCancellation, m.CurrentMode);
    }

    [Fact]
    public async Task Silence_is_not_success_and_state_stays_unknown()
    {
        var (_, _, m) = await Setup(_ => null);
        var r = await m.SetModeAsync(ListeningMode.Transparency);
        Assert.Equal(NoiseControlOutcome.NoResponse, r.Outcome);
        Assert.Null(m.CurrentMode);
    }

    [Fact]
    public async Task Off_sends_allow_off_first_on_pro2_and_reports_rejection()
    {
        // Device acknowledges 0x34 but stays in ANC for 0x0D 01.
        var (t, _, m) = await Setup(p =>
            IsControl(p, ControlId.AllowOffOption) ? p :
            IsControl(p, ControlId.ListeningMode) ? AapPackets.SetListeningMode(ListeningMode.NoiseCancellation) : null);
        var r = await m.SetModeAsync(ListeningMode.Off);
        Assert.Equal(AapPackets.SetAllowOffOption(true), t.Sent[3]);
        Assert.Equal(AapPackets.SetListeningMode(ListeningMode.Off), t.Sent[4]);
        Assert.Equal(NoiseControlOutcome.DeviceReportedDifferentMode, r.Outcome);
        Assert.Equal(ListeningMode.NoiseCancellation, r.Reported);
        Assert.True(m.OffOptionAllowed);
    }

    [Fact]
    public async Task Off_on_pro1_does_not_touch_allow_off()
    {
        var (t, _, m) = await Setup(p => IsControl(p, ControlId.ListeningMode) ? p : null, pid: 0x200E);
        var r = await m.SetModeAsync(ListeningMode.Off);
        Assert.True(r.Success);
        Assert.DoesNotContain(t.Sent, p => IsControl(p, ControlId.AllowOffOption));
    }

    [Fact]
    public async Task Unsupported_model_never_sends()
    {
        var (t, _, m) = await Setup(p => p, pid: 0x2002);
        var r = await m.SetModeAsync(ListeningMode.NoiseCancellation);
        Assert.Equal(NoiseControlOutcome.NotSupportedByModel, r.Outcome);
        Assert.Equal(3, t.Sent.Count);
    }

    [Fact]
    public async Task No_channel_is_reported()
    {
        var m = new NoiseControlManager();
        var r = await m.SetModeAsync(ListeningMode.Off);
        Assert.Equal(NoiseControlOutcome.NoControlChannel, r.Outcome);
    }

    [Fact]
    public async Task Link_loss_during_wait_is_transport_error()
    {
        var (t, _, m) = await Setup(_ => null);
        m.ConfirmationTimeout = TimeSpan.FromSeconds(5);
        var task = m.SetModeAsync(ListeningMode.Transparency);
        await Task.Delay(50);
        t.Drop();
        var r = await task;
        Assert.Equal(NoiseControlOutcome.TransportError, r.Outcome);
    }

    [Fact]
    public async Task Unsolicited_notification_updates_mode()
    {
        var (t, _, m) = await Setup(_ => null);
        ListeningMode? seen = null;
        m.ModeChanged += x => seen = x;
        t.Inject(AapPackets.SetListeningMode(ListeningMode.Transparency));
        Assert.Equal(ListeningMode.Transparency, m.CurrentMode);
        Assert.Equal(ListeningMode.Transparency, seen);
    }
}

public class BatteryMonitorTests
{
    static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    [Fact]
    public void Starts_unknown()
    {
        var b = new BatteryMonitor();
        Assert.Null(b.Current.Left.Level);
        Assert.Null(b.Current.Case.Level);
    }

    [Fact]
    public void Aap_wins_over_ble_and_ages_into_stale_not_zero()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var b = new BatteryMonitor(() => now);
        b.OnBleAdvertisement(new ProximityPairingMessage(0x2014, 80, 70, 50, false, false, false, true, true, false, false, 0));
        Assert.Equal(80, b.Current.Left.Level);
        Assert.Equal(BatterySource.Ble, b.Current.Left.Source);

        b.OnAapBattery((BatteryMessage)AapParser.Parse(Hex("04 00 04 00 04 00 03 02 01 64 02 01 04 01 63 01 01 08 01 11 02 01")));
        Assert.Equal(99, b.Current.Left.Level);
        Assert.True(b.Current.Left.Charging);
        Assert.Equal(BatterySource.Aap, b.Current.Left.Source);

        Assert.False(b.Current.Left.Stale);

        now += TimeSpan.FromMinutes(20); // AAP stale, BLE stale too: keep the newest value, flagged, with its time
        Assert.Equal(99, b.Current.Left.Level);
        Assert.True(b.Current.Left.Stale);
        Assert.Equal(BatterySource.Aap, b.Current.Left.Source);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), b.Current.Left.UpdatedAt);
    }

    [Fact]
    public void Newer_ble_value_is_shown_fresh_over_stale_aap()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var b = new BatteryMonitor(() => now);
        b.OnAapBattery((BatteryMessage)AapParser.Parse(Hex("04 00 04 00 04 00 03 02 01 64 02 01 04 01 63 01 01 08 01 11 02 01")));
        now += TimeSpan.FromMinutes(20);
        b.OnBleAdvertisement(new ProximityPairingMessage(0x2014, 80, 70, 50, false, false, false, true, true, false, false, 0));
        Assert.Equal(80, b.Current.Left.Level);
        Assert.False(b.Current.Left.Stale);
        Assert.Equal(now, b.LastBleAt);
    }

    [Fact]
    public void Radio_loss_marks_values_stale_without_erasing_them()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var b = new BatteryMonitor(() => now);
        b.OnBleAdvertisement(new ProximityPairingMessage(0x2014, 80, 70, 50, false, false, false, true, true, false, false, 0));
        now += TimeSpan.FromSeconds(5);
        b.MarkAllStale();
        Assert.Equal(80, b.Current.Left.Level);
        Assert.True(b.Current.Left.Stale);
        now += TimeSpan.FromSeconds(5);
        b.OnBleAdvertisement(new ProximityPairingMessage(0x2014, 70, 70, 50, false, false, false, true, true, false, false, 0));
        Assert.False(b.Current.Left.Stale);   // a new advertisement after the loss is fresh again
        Assert.Equal(70, b.Current.Left.Level);
    }

    [Fact]
    public void Unknown_stays_unknown_never_zero()
    {
        var b = new BatteryMonitor();
        b.MarkAllStale();
        Assert.Null(b.Current.Left.Level);
        Assert.False(b.Current.Left.IsKnown);
    }

    [Fact]
    public void Ble_missing_value_does_not_erase_fresh_value()
    {
        var b = new BatteryMonitor();
        b.OnBleAdvertisement(new ProximityPairingMessage(0x2014, 80, 70, 50, false, false, false, true, true, false, false, 0));
        b.OnBleAdvertisement(new ProximityPairingMessage(0x2014, 80, 70, null, false, false, false, true, true, false, false, 0));
        Assert.Equal(50, b.Current.Case.Level);
    }
}

public class AdvertisementFilterTests
{
    static ProximityPairingMessage Msg(ushort id) => new(id, 50, 50, 50, false, false, false, false, false, false, false, 0);

    [Fact]
    public void Filters_by_model_and_rssi()
    {
        var f = new AdvertisementFilter { ExpectedModelId = 0x2027 };
        Assert.False(f.Accept(Msg(0x2014), -40));
        Assert.False(f.Accept(Msg(0x2027), -90));
        Assert.True(f.Accept(Msg(0x2027), -50));
        Assert.False(f.Accept(Msg(0x2027), -68)); // much weaker second device nearby
    }

    [Fact]
    public void Without_bound_device_nothing_is_accepted()
    {
        var f = new AdvertisementFilter();
        Assert.False(f.Accept(Msg(0x2027), -30));
    }
}
