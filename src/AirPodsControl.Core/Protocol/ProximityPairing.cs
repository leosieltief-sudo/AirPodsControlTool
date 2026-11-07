namespace AirPodsControl.Core.Protocol;

/// <summary>
/// Apple Continuity "Proximity Pairing" BLE advertisement (manufacturer data, company 0x004C, type 0x07).
/// Layout from SpriteOvO/AirPodsDesktop Source/Core/AppleCP.h (packed struct, 27 bytes, buffer starts at the type byte):
///  0 type(0x07) | 1 remaining length (25) | 2 unk | 3-4 model id (LE) | 5 flags | 6 battery nibbles (lo=curr, hi=other)
///  7 lo=case battery, bit4 curr charging, bit5 other charging, bit6 case charging | 8 lid | 9 color | 10 unk | 11-26 encrypted
/// "curr" = the bud that broadcasts this advertisement; flags bit5 == 1 means the LEFT bud broadcasts.
/// Battery nibble 0..10 = value*10 %, anything else = unavailable.
/// This channel is readable from user mode on Windows without any driver; precision is 10%.
/// </summary>
public sealed record ProximityPairingMessage(
    ushort ModelId,
    int? LeftPercent, int? RightPercent, int? CasePercent,
    bool LeftCharging, bool RightCharging, bool CaseCharging,
    bool LeftInEar, bool RightInEar, bool BothInCase, bool LidOpen, byte Color)
{
    public const ushort AppleCompanyId = 0x004C;
    public const byte ProximityPairingType = 0x07;

    public static ProximityPairingMessage? TryParse(ReadOnlySpan<byte> d)
    {
        // Minimal length covering all unencrypted fields; type and length byte must be consistent.
        if (d.Length < 11 || d[0] != ProximityPairingType || d[1] != d.Length - 2) return null;

        ushort model = (ushort)(d[3] | (d[4] << 8));
        byte flags = d[5];
        bool currInEar = (flags & 0x02) != 0;
        bool bothInCase = (flags & 0x04) != 0;
        bool otherInEar = (flags & 0x08) != 0;
        bool leftBroadcasts = (flags & 0x20) != 0;

        int curr = d[6] & 0x0F, other = (d[6] >> 4) & 0x0F, caseLvl = d[7] & 0x0F;
        bool currChg = (d[7] & 0x10) != 0, otherChg = (d[7] & 0x20) != 0, caseChg = (d[7] & 0x40) != 0;
        bool lidOpen = (d[8] & 0x08) == 0;

        static int? Pct(int v) => v is >= 0 and <= 10 ? v * 10 : null;

        int? left = Pct(leftBroadcasts ? curr : other);
        int? right = Pct(leftBroadcasts ? other : curr);
        bool leftChg = leftBroadcasts ? currChg : otherChg;
        bool rightChg = leftBroadcasts ? otherChg : currChg;
        // AirPodsDesktop: in-ear flag is unreliable while charging, so it is filtered.
        bool leftInEar = !leftChg && (leftBroadcasts ? currInEar : otherInEar);
        bool rightInEar = !rightChg && (leftBroadcasts ? otherInEar : currInEar);

        return new ProximityPairingMessage(model, left, right, Pct(caseLvl),
            left is null ? false : leftChg, right is null ? false : rightChg, Pct(caseLvl) is null ? false : caseChg,
            leftInEar, rightInEar, bothInCase, lidOpen, d[9]);
    }
}
