namespace AirPodsControl.Core.Protocol;

/// <summary>
/// Listening mode values as they appear on the wire (control command 0x0D).
/// Source: LibrePods docs/control_commands.md ("1 = Off, 2 = noise cancellation, 3 = transparency, 4 = adaptive")
/// and linux/airpods_packets.h (createCommand(0x0D, 0x01..0x04)).
/// </summary>
public enum ListeningMode : byte
{
    Off = 0x01,
    NoiseCancellation = 0x02,
    Transparency = 0x03,
    Adaptive = 0x04,
}

/// <summary>AAP opcodes (16-bit little endian at bytes 4..5). Source: LibrePods docs/opcodes.md.</summary>
public static class AapOpcode
{
    public const ushort Battery = 0x0004;
    public const ushort EarDetection = 0x0006;
    public const ushort ControlCommand = 0x0009;
    public const ushort RequestNotifications = 0x000F;
    public const ushort Metadata = 0x001D;
    public const ushort FeatureFlags = 0x004D;
}

/// <summary>Control command identifiers (byte 6 of an opcode 0x09 packet). Source: LibrePods docs/control_commands.md.</summary>
public static class ControlId
{
    public const byte ListeningMode = 0x0D;
    /// <summary>Press-and-hold action per stem: data1 = right bud, data2 = left bud; 0x01 noise control, 0x05 Siri.</summary>
    public const byte ClickHoldMode = 0x16;
    /// <summary>Modes the press-and-hold gesture cycles through: bitmask Off 0x01, ANC 0x02, Transparency 0x04, Adaptive 0x08.</summary>
    public const byte ListeningModeConfigs = 0x1A;
    public const byte ConversationAwareness = 0x28;
    public const byte AllowOffOption = 0x34;
}

/// <summary>
/// Builders for the Apple Accessory Protocol (AAP / "AACP") packets sent over L2CAP PSM 0x1001.
/// Every byte sequence here is taken from LibrePods (librepods-org/librepods, docs + linux/airpods_packets.h
/// + android AACPManager.kt); nothing is invented. See docs/PROTOCOL.md for citations.
/// </summary>
public static class AapPackets
{
    public const ushort L2capPsm = 0x1001;

    /// <summary>SDP service UUID the AirPods register for AAP ("AAP Server" node in Device Manager).</summary>
    public static readonly Guid AapServiceUuid = new("74ec2172-0bad-4d01-8f77-997b2be0722a");

    /// <summary>Must be sent first; otherwise "the AirPods will not respond to any packets".</summary>
    public static byte[] Handshake() =>
        [0x00, 0x00, 0x04, 0x00, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    /// <summary>Host capabilities / feature flags (0x4D). LibrePods code uses 0xD7 (doc capture shows 0xFF).</summary>
    public static byte[] SetFeatureFlags() =>
        [0x04, 0x00, 0x04, 0x00, 0x4D, 0x00, 0xD7, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    /// <summary>Register for notifications (battery, ear detection, listening mode, ...).</summary>
    public static byte[] RequestNotifications() =>
        [0x04, 0x00, 0x04, 0x00, 0x0F, 0x00, 0xFF, 0xFF, 0xFF, 0xFF];

    /// <summary>Generic control command: 04 00 04 00 09 00 [id] [d1] [d2] [d3] [d4].</summary>
    public static byte[] ControlCommand(byte identifier, byte d1, byte d2 = 0, byte d3 = 0, byte d4 = 0) =>
        [0x04, 0x00, 0x04, 0x00, 0x09, 0x00, identifier, d1, d2, d3, d4];

    public static byte[] SetListeningMode(ListeningMode mode) => ControlCommand(ControlId.ListeningMode, (byte)mode);

    /// <summary>0x34 "Allow Off Option for Listening Mode config": 0x01 enabled, 0x02 disabled.</summary>
    public static byte[] SetAllowOffOption(bool enabled) => ControlCommand(ControlId.AllowOffOption, enabled ? (byte)0x01 : (byte)0x02);

    /// <summary>0x1A "ListeningModeConfigs": the press-and-hold cycle as a bitmask (see <see cref="ControlId.ListeningModeConfigs"/>).
    /// LibrePods docs/AAP Definitions.md "Configure Stem Long Press": e.g. 1A 0B = Off + ANC + Adaptive.</summary>
    public static byte[] SetListeningModeCycle(byte mask) => ControlCommand(ControlId.ListeningModeConfigs, mask);

    public static string ToHex(ReadOnlySpan<byte> data) => Convert.ToHexString(data);

    public static string ToSpacedHex(ReadOnlySpan<byte> data)
    {
        var sb = new System.Text.StringBuilder(data.Length * 3);
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        return sb.ToString();
    }
}
