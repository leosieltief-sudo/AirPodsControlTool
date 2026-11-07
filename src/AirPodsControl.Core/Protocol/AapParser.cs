using System.Text;

namespace AirPodsControl.Core.Protocol;

public enum BatteryComponent : byte { Right = 0x02, Left = 0x04, Case = 0x08 }

/// <summary>Battery status byte. LibrePods Packets.kt: CHARGING=1, NOT_CHARGING=2, DISCONNECTED=4, OPTIMIZED_CHARGING=5.</summary>
public enum BatteryStatus : byte { Unknown = 0x00, Charging = 0x01, NotCharging = 0x02, Disconnected = 0x04, OptimizedCharging = 0x05 }

public enum EarState : byte { InEar = 0x00, OutOfEar = 0x01, InCase = 0x02, Unknown = 0xFF }

public abstract record AapMessage(byte[] Raw);

public sealed record BatteryEntry(BatteryComponent Component, int? Level, BatteryStatus Status)
{
    public bool IsCharging => Status is BatteryStatus.Charging or BatteryStatus.OptimizedCharging;
}

public sealed record BatteryMessage(byte[] Raw, IReadOnlyList<BatteryEntry> Entries) : AapMessage(Raw);

public sealed record EarDetectionMessage(byte[] Raw, EarState Primary, EarState Secondary) : AapMessage(Raw);

public sealed record ControlCommandMessage(byte[] Raw, byte Identifier, byte Value1, byte Value2, byte Value3, byte Value4) : AapMessage(Raw)
{
    public ListeningMode? AsListeningMode =>
        Identifier == ControlId.ListeningMode && Value1 is >= 1 and <= 4 ? (ListeningMode)Value1 : null;
}

public sealed record MetadataMessage(byte[] Raw, string? Name, string? ModelNumber, string? Manufacturer, string? SerialNumber, string? FirmwareVersion) : AapMessage(Raw);

public sealed record UnknownMessage(byte[] Raw, ushort? Opcode) : AapMessage(Raw);

/// <summary>
/// Parses AAP packets received from the AirPods. Layouts from LibrePods docs/AAP Definitions.md and Packets.kt.
/// Parsing is defensive: malformed packets become <see cref="UnknownMessage"/> instead of throwing.
/// </summary>
public static class AapParser
{
    public static bool HasDataHeader(ReadOnlySpan<byte> d) =>
        d.Length >= 6 && d[0] == 0x04 && d[1] == 0x00 && d[2] == 0x04 && d[3] == 0x00;

    public static ushort? GetOpcode(ReadOnlySpan<byte> d) => HasDataHeader(d) ? (ushort)(d[4] | (d[5] << 8)) : null;

    public static AapMessage Parse(byte[] data)
    {
        var opcode = GetOpcode(data);
        try
        {
            return opcode switch
            {
                AapOpcode.Battery => (AapMessage?)ParseBattery(data) ?? new UnknownMessage(data, opcode),
                AapOpcode.EarDetection when data.Length >= 8 =>
                    new EarDetectionMessage(data, ToEar(data[6]), ToEar(data[7])),
                AapOpcode.ControlCommand when data.Length >= 11 =>
                    new ControlCommandMessage(data, data[6], data[7], data[8], data[9], data[10]),
                AapOpcode.ControlCommand when data.Length >= 8 =>
                    new ControlCommandMessage(data, data[6], data[7], 0, 0, 0),
                AapOpcode.Metadata => (AapMessage?)ParseMetadata(data) ?? new UnknownMessage(data, opcode),
                _ => new UnknownMessage(data, opcode),
            };
        }
        catch (Exception)
        {
            return new UnknownMessage(data, opcode);
        }
    }

    /// <summary>04 00 04 00 04 00 [count] ([component] 01 [level] [status] 01) * count</summary>
    internal static BatteryMessage? ParseBattery(byte[] d)
    {
        if (d.Length < 7) return null;
        int count = d[6];
        if (count is < 1 or > 3 || d.Length < 7 + count * 5) return null;
        var list = new List<BatteryEntry>(count);
        for (int i = 0; i < count; i++)
        {
            int o = 7 + i * 5;
            byte comp = d[o];
            if (comp is not (0x02 or 0x04 or 0x08)) return null;
            byte level = d[o + 2];
            var status = Enum.IsDefined(typeof(BatteryStatus), d[o + 3]) ? (BatteryStatus)d[o + 3] : BatteryStatus.Unknown;
            // A level is only trustworthy if it is a valid percentage and the component is not reported disconnected.
            int? lvl = level <= 100 && status != BatteryStatus.Disconnected ? level : null;
            list.Add(new BatteryEntry((BatteryComponent)comp, lvl, status));
        }
        return new BatteryMessage(d, list);
    }

    /// <summary>
    /// 04 00 04 00 1D 00 [5 unknown bytes] then NUL-terminated UTF-8 strings:
    /// name, model number, manufacturer, serial, firmware, ... (LibrePods docs/device-info.md).
    /// </summary>
    internal static MetadataMessage? ParseMetadata(byte[] d)
    {
        const int stringsStart = 11;
        if (d.Length <= stringsStart) return null;
        var fields = new List<string>();
        int start = stringsStart;
        for (int i = stringsStart; i < d.Length && fields.Count < 6; i++)
        {
            if (d[i] == 0x00)
            {
                fields.Add(Encoding.UTF8.GetString(d, start, i - start));
                start = i + 1;
            }
        }
        if (fields.Count < 2) return null;
        string? F(int i) => i < fields.Count && fields[i].Length > 0 ? fields[i] : null;
        return new MetadataMessage(d, F(0), F(1), F(2), F(3), F(4));
    }

    private static EarState ToEar(byte b) => b switch
    {
        0x00 => EarState.InEar,
        0x01 => EarState.OutOfEar,
        0x02 => EarState.InCase,
        _ => EarState.Unknown,
    };
}
