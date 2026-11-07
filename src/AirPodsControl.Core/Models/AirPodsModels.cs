using AirPodsControl.Core.Protocol;

namespace AirPodsControl.Core.Models;

public enum ModelShape { Standard, Pro, Max, Beats }

/// <summary>Tri-state support flag: we never claim support we cannot back with a source.</summary>
public enum Support { No, Yes, Unverified }

/// <summary>How "Off" can be reached on this model.</summary>
public enum OffModeRequirement
{
    NotApplicable,
    /// <summary>Off is always a valid listening mode (AirPods Pro 1, AirPods Max).</summary>
    Native,
    /// <summary>Off must be allowed via control 0x34 ("Allow Off Option") first (Pro 2 / Pro 3 / 4 ANC on recent firmware).</summary>
    RequiresAllowOffOption,
    Unknown,
}

public sealed record AirPodsModelInfo(
    string Name,
    ushort ProductId,
    ModelShape Shape,
    Support NoiseControl,
    Support Adaptive,
    OffModeRequirement OffMode,
    string[] ModelNumbers)
{
    public bool HasNoiseControl => NoiseControl != Support.No;
}

/// <summary>
/// Model catalog. Product IDs: AirPodsDesktop AppleCP.cpp GetModel() (also used as BTHENUM PID).
/// Model numbers: LibrePods AirPods.kt / linux enums.h. Capabilities: LibrePods AirPods.kt (LISTENING_MODE, ADAPTIVE_AUDIO).
/// </summary>
public static class AirPodsCatalog
{
    public static readonly IReadOnlyList<AirPodsModelInfo> All =
    [
        new("AirPods (1st gen)", 0x2002, ModelShape.Standard, Support.No, Support.No, OffModeRequirement.NotApplicable, ["A1523", "A1722"]),
        new("AirPods (2nd gen)", 0x200F, ModelShape.Standard, Support.No, Support.No, OffModeRequirement.NotApplicable, ["A2032", "A2031"]),
        new("AirPods (3rd gen)", 0x2013, ModelShape.Standard, Support.No, Support.No, OffModeRequirement.NotApplicable, ["A2565", "A2564"]),
        new("AirPods 4", 0x2019, ModelShape.Standard, Support.No, Support.No, OffModeRequirement.NotApplicable, ["A3053", "A3050", "A3054"]),
        new("AirPods 4 (ANC)", 0x201B, ModelShape.Standard, Support.Yes, Support.Yes, OffModeRequirement.RequiresAllowOffOption, ["A3056", "A3055", "A3057"]),
        new("AirPods Pro", 0x200E, ModelShape.Pro, Support.Yes, Support.No, OffModeRequirement.Native, ["A2084", "A2083"]),
        new("AirPods Pro 2", 0x2014, ModelShape.Pro, Support.Yes, Support.Yes, OffModeRequirement.RequiresAllowOffOption, ["A2931", "A2699", "A2698"]),
        new("AirPods Pro 2 (USB-C)", 0x2024, ModelShape.Pro, Support.Yes, Support.Yes, OffModeRequirement.RequiresAllowOffOption, ["A3047", "A3048", "A3049"]),
        new("AirPods Pro 3", 0x2027, ModelShape.Pro, Support.Yes, Support.Yes, OffModeRequirement.RequiresAllowOffOption, ["A3063", "A3064", "A3065"]),
        new("AirPods Max", 0x200A, ModelShape.Max, Support.Unverified, Support.No, OffModeRequirement.Native, ["A2096"]),
        new("AirPods Max (USB-C)", 0x201F, ModelShape.Max, Support.Unverified, Support.No, OffModeRequirement.Native, ["A3184"]),
        new("Beats Fit Pro", 0x2012, ModelShape.Beats, Support.Unverified, Support.No, OffModeRequirement.Unknown, []),
    ];

    public static AirPodsModelInfo? ByProductId(ushort pid) => All.FirstOrDefault(m => m.ProductId == pid);

    public static AirPodsModelInfo? ByModelNumber(string? number) =>
        number is null ? null : All.FirstOrDefault(m => m.ModelNumbers.Contains(number, StringComparer.OrdinalIgnoreCase));

    /// <summary>Fallback for an Apple audio device we don't know: allow trying controls, but flag everything as unverified.</summary>
    public static AirPodsModelInfo Unknown(ushort pid) =>
        new($"Apple audio device (PID 0x{pid:X4})", pid, ModelShape.Pro, Support.Unverified, Support.Unverified, OffModeRequirement.Unknown, []);

    public static bool SupportsMode(AirPodsModelInfo m, ListeningMode mode) => mode switch
    {
        ListeningMode.Adaptive => m.Adaptive != Support.No,
        _ => m.NoiseControl != Support.No,
    };
}
