using AirPodsControl.Core.Models;
using AirPodsControl.Core.Protocol;
using Xunit;

namespace AirPodsControl.Tests;

/// <summary>
/// Test vectors are taken verbatim from LibrePods docs ("AAP Definitions.md", control_commands.md, device-info.md)
/// and from linux/airpods_packets.h. Where a vector is constructed from a documented layout rather than captured,
/// the test name says so.
/// </summary>
public class AapPacketTests
{
    static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    [Fact]
    public void Handshake_matches_librepods() =>
        Assert.Equal(Hex("00 00 04 00 01 00 02 00 00 00 00 00 00 00 00 00"), AapPackets.Handshake());

    [Fact]
    public void FeatureFlags_matches_librepods_linux_constant() =>
        Assert.Equal(Hex("040004004d00d700000000000000"), AapPackets.SetFeatureFlags());

    [Fact]
    public void RequestNotifications_matches_doc_variant() =>
        Assert.Equal(Hex("04 00 04 00 0F 00 FF FF FF FF"), AapPackets.RequestNotifications());

    [Theory]
    [InlineData(ListeningMode.Off, "04 00 04 00 09 00 0D 01 00 00 00")]
    [InlineData(ListeningMode.NoiseCancellation, "04 00 04 00 09 00 0D 02 00 00 00")]
    [InlineData(ListeningMode.Transparency, "04 00 04 00 09 00 0D 03 00 00 00")]
    [InlineData(ListeningMode.Adaptive, "04 00 04 00 09 00 0D 04 00 00 00")]
    public void ListeningMode_packets(ListeningMode mode, string expected) =>
        Assert.Equal(Hex(expected), AapPackets.SetListeningMode(mode));

    [Fact]
    public void AllowOffOption_packets()
    {
        Assert.Equal(Hex("04 00 04 00 09 00 34 01 00 00 00"), AapPackets.SetAllowOffOption(true));
        Assert.Equal(Hex("04 00 04 00 09 00 34 02 00 00 00"), AapPackets.SetAllowOffOption(false));
    }

    [Fact]
    public void Psm_and_service_uuid()
    {
        Assert.Equal(0x1001, AapPackets.L2capPsm);
        Assert.Equal(new Guid("74ec2172-0bad-4d01-8f77-997b2be0722a"), AapPackets.AapServiceUuid);
    }
}

public class AapParserTests
{
    static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    [Fact]
    public void Battery_doc_example()
    {
        // LibrePods "AAP Definitions.md" battery example.
        var msg = Assert.IsType<BatteryMessage>(AapParser.Parse(Hex("04 00 04 00 04 00 03 02 01 64 02 01 04 01 63 01 01 08 01 11 02 01")));
        Assert.Equal(3, msg.Entries.Count);
        var right = msg.Entries.Single(e => e.Component == BatteryComponent.Right);
        var left = msg.Entries.Single(e => e.Component == BatteryComponent.Left);
        var @case = msg.Entries.Single(e => e.Component == BatteryComponent.Case);
        Assert.Equal(100, right.Level); Assert.False(right.IsCharging);
        Assert.Equal(99, left.Level); Assert.True(left.IsCharging);
        Assert.Equal(17, @case.Level); Assert.False(@case.IsCharging);
    }

    [Fact]
    public void Battery_disconnected_component_has_no_level()
    {
        var msg = Assert.IsType<BatteryMessage>(AapParser.Parse(Hex("04 00 04 00 04 00 01 08 01 32 04 01")));
        Assert.Null(msg.Entries[0].Level);
    }

    [Theory]
    [InlineData("04 00 04 00 04 00 03 02 01 64")]          // truncated
    [InlineData("04 00 04 00 04 00 01 07 01 64 02 01")]    // invalid component
    [InlineData("04 00 04 00 04 00 01 02 01 C8 02 01")]    // 200 % is not a valid level -> parsed but null
    public void Battery_malformed_is_never_a_fake_value(string hex)
    {
        var m = AapParser.Parse(Hex(hex));
        if (m is BatteryMessage b) Assert.All(b.Entries, e => Assert.Null(e.Level));
        else Assert.IsType<UnknownMessage>(m);
    }

    [Theory]
    [InlineData("04 00 04 00 09 00 0D 01 00 00 00", ListeningMode.Off)]
    [InlineData("04 00 04 00 09 00 0D 02 00 00 00", ListeningMode.NoiseCancellation)]
    [InlineData("04 00 04 00 09 00 0D 03 00 00 00", ListeningMode.Transparency)]
    [InlineData("04 00 04 00 09 00 0D 04 00 00 00", ListeningMode.Adaptive)]
    public void ListeningMode_notification(string hex, ListeningMode expected)
    {
        var c = Assert.IsType<ControlCommandMessage>(AapParser.Parse(Hex(hex)));
        Assert.Equal(ControlId.ListeningMode, c.Identifier);
        Assert.Equal(expected, c.AsListeningMode);
    }

    [Fact]
    public void ListeningMode_out_of_range_is_null()
    {
        var c = Assert.IsType<ControlCommandMessage>(AapParser.Parse(Hex("04 00 04 00 09 00 0D 09 00 00 00")));
        Assert.Null(c.AsListeningMode);
    }

    [Fact]
    public void ListeningModeConfigs_doc_example()
    {
        // "04 00 04 00 09 00 1A 0B 00 00 00 - Turns on Adaptive from O and ANC" (bitmask Off|ANC|Adaptive)
        var c = Assert.IsType<ControlCommandMessage>(AapParser.Parse(Hex("04 00 04 00 09 00 1A 0B 00 00 00")));
        Assert.Equal(ControlId.ListeningModeConfigs, c.Identifier);
        Assert.Equal(0x0B, c.Value1);
        Assert.Null(c.AsListeningMode);
    }

    [Theory]
    [InlineData("04 00 04 00 06 00 00 00", EarState.InEar, EarState.InEar)]
    [InlineData("04 00 04 00 06 00 01 02", EarState.OutOfEar, EarState.InCase)]
    public void EarDetection(string hex, EarState p, EarState s)
    {
        var e = Assert.IsType<EarDetectionMessage>(AapParser.Parse(Hex(hex)));
        Assert.Equal(p, e.Primary); Assert.Equal(s, e.Secondary);
    }

    [Fact]
    public void Metadata_doc_example_prefix()
    {
        // Prefix of the LibrePods metadata example ("AirPods Pro", "A3048", "Apple Inc."), terminated after manufacturer.
        var raw = Hex("040004001d0002d5000400416972506f64732050726f004133303438004170706c6520496e632e00");
        var m = Assert.IsType<MetadataMessage>(AapParser.Parse(raw));
        Assert.Equal("AirPods Pro", m.Name);
        Assert.Equal("A3048", m.ModelNumber);
        Assert.Equal("Apple Inc.", m.Manufacturer);
        Assert.Equal("AirPods Pro 2 (USB-C)", AirPodsCatalog.ByModelNumber(m.ModelNumber)!.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("04 00 04 00")]
    [InlineData("01 02 03 04 05 06 07 08 09")]
    public void Garbage_is_unknown(string hex) =>
        Assert.IsType<UnknownMessage>(AapParser.Parse(hex.Length == 0 ? [] : Hex(hex)));
}

public class ProximityPairingTests
{
    // Constructed from the documented AirPodsDesktop AppleCP.h layout (not a captured advertisement).
    static byte[] Build(ushort model, byte flags, byte pods, byte caseByte, byte lid = 0x00)
    {
        var d = new byte[27];
        d[0] = 0x07; d[1] = 25; d[2] = 0x01;
        d[3] = (byte)(model & 0xFF); d[4] = (byte)(model >> 8);
        d[5] = flags; d[6] = pods; d[7] = caseByte; d[8] = lid;
        return d;
    }

    [Fact]
    public void Left_broadcasting_maps_curr_to_left()
    {
        // flags bit5 = left broadcasts; curr(lo)=8 -> left 80 %, other(hi)=6 -> right 60 %, case 5 -> 50 %, case charging
        var m = ProximityPairingMessage.TryParse(Build(0x2014, 0x20, 0x68, 0x45))!;
        Assert.Equal((ushort)0x2014, m.ModelId);
        Assert.Equal(80, m.LeftPercent); Assert.Equal(60, m.RightPercent); Assert.Equal(50, m.CasePercent);
        Assert.True(m.CaseCharging); Assert.False(m.LeftCharging); Assert.False(m.RightCharging);
        Assert.True(m.LidOpen);
    }

    [Fact]
    public void Right_broadcasting_maps_curr_to_right()
    {
        var m = ProximityPairingMessage.TryParse(Build(0x2027, 0x00, 0x68, 0x1F, lid: 0x08))!;
        Assert.Equal(60, m.LeftPercent); Assert.Equal(80, m.RightPercent);
        Assert.Null(m.CasePercent);           // nibble 15 = unavailable
        Assert.True(m.RightCharging);         // bit4 = curr (right) charging
        Assert.False(m.CaseCharging);
        Assert.False(m.LidOpen);
    }

    [Fact]
    public void Unavailable_nibbles_are_null_not_numbers()
    {
        var m = ProximityPairingMessage.TryParse(Build(0x2024, 0x20, 0xFF, 0x7F))!;
        Assert.Null(m.LeftPercent); Assert.Null(m.RightPercent); Assert.Null(m.CasePercent);
        Assert.False(m.LeftCharging); Assert.False(m.CaseCharging);
    }

    [Fact]
    public void Wrong_type_or_length_rejected()
    {
        var d = Build(0x2014, 0, 0x55, 0x05); d[0] = 0x10;
        Assert.Null(ProximityPairingMessage.TryParse(d));
        var e = Build(0x2014, 0, 0x55, 0x05); e[1] = 3;
        Assert.Null(ProximityPairingMessage.TryParse(e));
        Assert.Null(ProximityPairingMessage.TryParse(new byte[5]));
    }

    [Theory]
    [InlineData(0x2002, "AirPods (1st gen)")]
    [InlineData(0x200E, "AirPods Pro")]
    [InlineData(0x2014, "AirPods Pro 2")]
    [InlineData(0x2024, "AirPods Pro 2 (USB-C)")]
    [InlineData(0x2027, "AirPods Pro 3")]
    [InlineData(0x201B, "AirPods 4 (ANC)")]
    public void Catalog_product_ids(int pid, string name) =>
        Assert.Equal(name, AirPodsCatalog.ByProductId((ushort)pid)!.Name);

    [Fact]
    public void Non_anc_models_do_not_claim_noise_control()
    {
        Assert.False(AirPodsCatalog.ByProductId(0x2002)!.HasNoiseControl);
        Assert.False(AirPodsCatalog.ByProductId(0x2019)!.HasNoiseControl);
        Assert.False(AirPodsCatalog.SupportsMode(AirPodsCatalog.ByProductId(0x200E)!, ListeningMode.Adaptive));
    }
}
