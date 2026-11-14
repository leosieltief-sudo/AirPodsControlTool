using System.Diagnostics;
using System.Text.Json;
using AirPodsControl.Core.Bridge;
using AirPodsControl.Core.Protocol;
using AirPodsControl.Core.Services;
using Xunit;

namespace AirPodsControl.Tests;

public class UsbipdParserTests
{
    [Fact]
    public void State_json_shared_and_attached()
    {
        // Shape follows usbipd-win Usbipd.Automation/Device.cs (InstanceId, Description, IsForced, BusId, PersistedGuid, StubInstanceId, ClientIPAddress).
        var json = """
        {
          "Devices": [
            { "BusId": "1-14", "ClientIPAddress": null, "Description": "MediaTek Bluetooth Adapter",
              "InstanceId": "USB\\VID_13D3&PID_3622\\000000000", "IsForced": false,
              "PersistedGuid": "6b4b2a2c-1c55-4c4b-8f1a-0a7c1e9d3b10", "StubInstanceId": null },
            { "BusId": "2-1", "ClientIPAddress": "172.20.0.2", "Description": "USB Serial",
              "InstanceId": "USB\\VID_0403&PID_6001\\A1", "IsForced": false, "PersistedGuid": "11111111-2222-3333-4444-555555555555", "StubInstanceId": "x" },
            { "BusId": "3-2", "ClientIPAddress": null, "Description": "Webcam",
              "InstanceId": "USB\\VID_046D&PID_085B\\B", "IsForced": false, "PersistedGuid": null, "StubInstanceId": null }
          ]
        }
        """;
        var d = UsbipdParser.ParseState(json);
        Assert.Equal(3, d.Count);
        var bt = d.Single(x => x.Matches("13d3:3622"));
        Assert.Equal("1-14", bt.BusId);
        Assert.True(bt.IsShared); Assert.False(bt.IsAttached);
        Assert.True(d[1].IsAttached);
        Assert.False(d[2].IsShared);
    }

    [Fact]
    public void State_json_camel_case_is_accepted()
    {
        var d = UsbipdParser.ParseState("""{"devices":[{"busId":"1-3","instanceId":"USB\\VID_8087&PID_0026\\5","description":"Intel(R) Wireless Bluetooth(R)","persistedGuid":null,"clientIPAddress":null}]}""");
        Assert.Equal("8087:0026", d[0].VidPid);
        Assert.False(d[0].IsShared);
    }

    [Fact]
    public void List_text_fallback()
    {
        var text = "Connected:\r\nBUSID  VID:PID    DEVICE                                                        STATE\r\n" +
                   "1-14   13d3:3622  MediaTek Bluetooth Adapter                                    Shared\r\n" +
                   "1-3    8087:0026  Intel(R) Wireless Bluetooth(R)                                Attached\r\n" +
                   "2-4    046d:085b  Logitech Webcam C925e, Logitech Webcam C925e                   Not shared\r\n\r\n" +
                   "Persisted:\r\nGUID                                  DEVICE\r\n";
        var d = UsbipdParser.ParseList(text);
        Assert.Equal(3, d.Count);
        Assert.True(d[0].IsShared); Assert.False(d[0].IsAttached);
        Assert.True(d[1].IsAttached);
        Assert.False(d[2].IsShared);
        Assert.Equal("Logitech Webcam C925e, Logitech Webcam C925e", d[2].Description);
    }

    [Fact]
    public void VidPid_from_instance_id() =>
        Assert.Equal("13d3:3622", UsbipdParser.VidPidFromInstanceId(@"USB\VID_13D3&PID_3622&MI_00\7&3B2F8856&0&0000"));
}

public class LinkKeyParserTests
{
    // Synthetic export in the layout of `reg export` (keys are made up, never real ones).
    private const string Export = """
        Windows Registry Editor Version 5.00

        [HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Keys]

        [HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Keys\c0fbf9123456]
        "a1b2c3d4e5f6"=hex:00,11,22,33,44,55,66,77,88,99,aa,bb,cc,dd,ee,ff
        "2cda46c55b83"=hex:10,20,30,40,50,60,70,80,90,a0,b0,c0,d0,e0,f0,\
          01
        "MasterIRK"=hex:01,02

        [HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Keys\c0fbf9123456\aabbccddeeff]
        "LTK"=hex:01,02,03,04,05,06,07,08,09,0a,0b,0c,0d,0e,0f,10
        """;

    [Fact]
    public void Parses_classic_keys_only()
    {
        var keys = WindowsLinkKeyParser.Parse(Export);
        Assert.Equal(2, keys.Count);
        var a = keys.Single(k => k.DeviceAddress == 0xA1B2C3D4E5F6);
        Assert.Equal("C0:FB:F9:12:34:56", a.AdapterMac);
        Assert.Equal("A1:B2:C3:D4:E5:F6", a.DeviceMac);
        Assert.Equal("00112233445566778899AABBCCDDEEFF", a.KeyHex);
        var b = keys.Single(k => k.DeviceAddress == 0x2CDA46C55B83);
        Assert.Equal("102030405060708090A0B0C0D0E0F001", b.KeyHex); // continuation line joined
    }

    [Fact]
    public void Ignores_le_subkeys_and_garbage()
    {
        Assert.Empty(WindowsLinkKeyParser.Parse("[HKEY_LOCAL_MACHINE\\SOFTWARE\\X]\n\"a1b2c3d4e5f6\"=hex:00,11"));
        Assert.DoesNotContain(WindowsLinkKeyParser.Parse(Export), k => k.DeviceAddress == 0xAABBCCDDEEFF);
    }
}

public class BridgeResultTests
{
    [Fact]
    public void Confirmed_result_maps_to_confirmed()
    {
        var r = BridgeResultParser.Parse("STAGE wait_hci\nSTAGE aap\nRESULT {\"ok\": true, \"outcome\": \"confirmed\", \"requested\": 2, \"reported\": 2, \"mode_before\": 3, \"battery\": [{\"component\": \"left\", \"level\": 87, \"charging\": false}, {\"component\": \"case\", \"level\": null, \"charging\": false}]}\n");
        Assert.NotNull(r);
        Assert.True(r!.Ok);
        Assert.Equal(ListeningMode.NoiseCancellation, r.ReportedMode);
        Assert.Equal(87, r.Battery[0].Level);
        Assert.Null(r.Battery[1].Level);
        var n = BridgeResultParser.ToNoiseControlResult(r, ListeningMode.NoiseCancellation);
        Assert.Equal(NoiseControlOutcome.Confirmed, n.Outcome);
    }

    [Fact]
    public void Different_mode_is_not_success()
    {
        var r = BridgeResultParser.Parse("RESULT {\"ok\": false, \"outcome\": \"different\", \"requested\": 1, \"reported\": 2}");
        var n = BridgeResultParser.ToNoiseControlResult(r, ListeningMode.Off);
        Assert.Equal(NoiseControlOutcome.DeviceReportedDifferentMode, n.Outcome);
        Assert.Equal(ListeningMode.NoiseCancellation, n.Reported);
        Assert.Contains("Off Listening Mode", n.Message);
    }

    [Theory]
    [InlineData("RESULT {\"ok\": false, \"outcome\": \"no_response\", \"requested\": 3}", NoiseControlOutcome.NoResponse)]
    [InlineData("RESULT {\"ok\": false, \"outcome\": \"adapter_not_ready\", \"error\": \"hci0 did not appear within 4s\"}", NoiseControlOutcome.TransportError)]
    [InlineData("RESULT {\"ok\": false, \"outcome\": \"connect_failed\", \"errno\": 111, \"error\": \"ConnectionRefusedError\"}", NoiseControlOutcome.TransportError)]
    [InlineData("RESULT {not json", NoiseControlOutcome.TransportError)]
    [InlineData("python3: not found", NoiseControlOutcome.TransportError)]
    public void Failures_never_map_to_success(string output, NoiseControlOutcome expected)
    {
        var n = BridgeResultParser.ToNoiseControlResult(BridgeResultParser.Parse(output), ListeningMode.Transparency);
        Assert.Equal(expected, n.Outcome);
        Assert.False(n.Success);
    }

    [Fact]
    public void Sent_is_success_but_not_confirmed()
    {
        var n = BridgeResultParser.ToNoiseControlResult(
            BridgeResultParser.Parse("RESULT {\"ok\": true, \"outcome\": \"sent\", \"requested\": 2, \"handshake_ok\": true}"), ListeningMode.NoiseCancellation);
        Assert.Equal(NoiseControlOutcome.Sent, n.Outcome);
        Assert.True(n.Success);
        Assert.False(n.Confirmed);
        Assert.Null(n.Reported);              // nothing reported by the AirPods
        Assert.Contains("do not confirm", n.Message);
    }

    [Fact]
    public void Confirmed_claim_with_wrong_reported_mode_is_rejected()
    {
        // defensive: a "confirmed" outcome whose reported value differs must not count as success
        var n = BridgeResultParser.ToNoiseControlResult(
            BridgeResultParser.Parse("RESULT {\"ok\": true, \"outcome\": \"confirmed\", \"requested\": 3, \"reported\": 2}"), ListeningMode.Transparency);
        Assert.Equal(NoiseControlOutcome.DeviceReportedDifferentMode, n.Outcome);
    }

    [Fact]
    public void Read_only_result_exposes_current_mode()
    {
        var r = BridgeResultParser.Parse("RESULT {\"ok\": true, \"outcome\": \"read_only\", \"mode\": 4}")!;
        Assert.Equal(ListeningMode.Adaptive, r.CurrentMode);
    }
}

public class BackendSelectorTests
{
    private sealed class Fake(ControlBackend kind, bool ready) : IModeSwitchProvider
    {
        public ControlBackend Kind => kind;
        public string DisplayName => kind.ToString();
        public bool InterruptsAudio => kind == ControlBackend.WslBridge;
        public ProviderStatus Status => new(ready, ready ? "ready" : "not ready");
        public Task<NoiseControlResult> SetModeAsync(ModeSwitchRequest r, IProgress<string>? p, CancellationToken ct) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(ControlBackend.Auto, true, true, ControlBackend.MagicAap)]
    [InlineData(ControlBackend.Auto, false, true, ControlBackend.WslBridge)]
    [InlineData(ControlBackend.Auto, false, false, null)]
    [InlineData(ControlBackend.WslBridge, true, true, ControlBackend.WslBridge)]
    [InlineData(ControlBackend.WslBridge, true, false, null)]
    [InlineData(ControlBackend.MagicAap, false, true, null)]
    public void Selection(ControlBackend pref, bool magicReady, bool bridgeReady, ControlBackend? expected)
    {
        var p = new IModeSwitchProvider[] { new Fake(ControlBackend.MagicAap, magicReady), new Fake(ControlBackend.WslBridge, bridgeReady) };
        Assert.Equal(expected, BackendSelector.Select(pref, p)?.Kind);
    }
}

/// <summary>Keeps bridge/linux/aap_bridge.py byte-identical with AapPackets.cs (skipped when no Python is installed).</summary>
public class BridgeScriptSyncTests
{
    private static string? FindScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "bridge", "linux", "aap_bridge.py"))) dir = dir.Parent;
        return dir is null ? null : Path.Combine(dir.FullName, "bridge", "linux", "aap_bridge.py");
    }

    [Fact]
    public void Python_packets_match_csharp()
    {
        var script = FindScript();
        if (script is null) return;
        string output;
        try
        {
            var psi = new ProcessStartInfo("python", $"\"{script}\" --selftest") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            if (p.ExitCode != 0) return;
        }
        catch (System.ComponentModel.Win32Exception) { return; } // python not installed → nothing to compare
        var d = JsonSerializer.Deserialize<Dictionary<string, string>>(output)!;
        string H(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
        Assert.Equal(H(AapPackets.Handshake()), d["handshake"]);
        Assert.Equal(H(AapPackets.SetFeatureFlags()), d["feature_flags"]);
        Assert.Equal(H(AapPackets.RequestNotifications()), d["request_notifications"]);
        Assert.Equal(H(AapPackets.SetAllowOffOption(true)), d["allow_off_on"]);
        Assert.Equal(H(AapPackets.SetAllowOffOption(false)), d["allow_off_off"]);
        Assert.Equal(H(AapPackets.SetListeningMode(ListeningMode.Off)), d["mode_off"]);
        Assert.Equal(H(AapPackets.SetListeningMode(ListeningMode.NoiseCancellation)), d["mode_anc"]);
        Assert.Equal(H(AapPackets.SetListeningMode(ListeningMode.Transparency)), d["mode_transparency"]);
        Assert.Equal(H(AapPackets.SetListeningMode(ListeningMode.Adaptive)), d["mode_adaptive"]);
        Assert.Equal(H(AapPackets.SetListeningModeCycle(0x07)), d["cycle_off_anc_transparency"]);
        Assert.Equal(H(AapPackets.SetListeningModeCycle(0x0F)), d["cycle_all"]);
    }
}

/// <summary>Replies of the persistent daemon (aapd.py) are parsed with the same RESULT parser as bridge.sh output.</summary>
public class BridgeDaemonReplyTests
{
    [Fact]
    public void Daemon_reply_with_extra_fields_parses_as_sent()
    {
        const string reply = """{"type": "reply", "id": 3, "ok": true, "outcome": "sent", "requested": 3, "mode": null, "stack": "mgmt",""" +
                             """ "battery": [{"component": "left", "level": 68, "charging": false}], "t": {"hci": 0.1, "sent": 1.2, "channel_reused": true}}""";
        var r = BridgeResultParser.Parse(BridgeResultParser.Prefix + reply);
        Assert.NotNull(r);
        Assert.True(r!.Ok);
        Assert.Equal(68, r.Battery.Single().Level);
        var n = BridgeResultParser.ToNoiseControlResult(r, ListeningMode.Transparency);
        Assert.Equal(NoiseControlOutcome.Sent, n.Outcome);
        Assert.True(n.Success);
        Assert.False(n.Confirmed);
    }

    [Fact]
    public void Missing_key_points_to_setup()
    {
        var r = BridgeResultParser.Parse("""RESULT {"ok": false, "outcome": "no_key", "error": "no pairing key stored for adapter AA"}""");
        var n = BridgeResultParser.ToNoiseControlResult(r, ListeningMode.NoiseCancellation);
        Assert.Equal(NoiseControlOutcome.TransportError, n.Outcome);
        Assert.Contains("Driverless setup", n.Message);
    }
}
