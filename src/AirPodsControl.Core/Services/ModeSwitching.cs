using AirPodsControl.Core.Models;
using AirPodsControl.Core.Protocol;

namespace AirPodsControl.Core.Services;

/// <summary>User preference for how listening-mode commands reach the AirPods.</summary>
public enum ControlBackend
{
    /// <summary>MagicAAP if its channel is open, otherwise the WSL2 bridge if it is set up.</summary>
    Auto,
    /// <summary>WSL2 + usbipd: no Test Mode, but the Bluetooth adapter is handed to Linux for a few seconds.</summary>
    WslBridge,
    /// <summary>MagicAAP kernel driver (Test Mode + Secure Boot off).</summary>
    MagicAap,
}

public sealed record ProviderStatus(bool Ready, string Message)
{
    public static ProviderStatus NotReady(string why) => new(false, why);
}

/// <param name="Initiator">Who asked (hotkey, tray, panel, cli…) — recorded in the operation trace.</param>
/// <param name="AllowInputLoss">Explicit approval to hand over an adapter that serves a Bluetooth mouse/keyboard
/// (CLI --allow-input-loss). Never implied by a general confirmation.</param>
public sealed record ModeSwitchRequest(ulong DeviceAddress, string DeviceName, AirPodsModelInfo? Model, ListeningMode Mode, bool AutoEnableOffOption,
    string Initiator = "app", bool AllowInputLoss = false);

/// <summary>Strategy for sending AAP listening-mode commands. Every implementation reports only confirmed state.</summary>
public interface IModeSwitchProvider
{
    ControlBackend Kind { get; }
    string DisplayName { get; }
    /// <summary>True when using the provider interrupts Windows Bluetooth audio (WSL bridge).</summary>
    bool InterruptsAudio { get; }
    ProviderStatus Status { get; }
    Task<NoiseControlResult> SetModeAsync(ModeSwitchRequest request, IProgress<string>? progress, CancellationToken ct);
}

public static class BackendSelector
{
    /// <summary>
    /// Picks the provider for a preference. Auto prefers the persistent MagicAAP channel (no audio interruption,
    /// live notifications) and falls back to the WSL bridge. Returns null when nothing usable is configured.
    /// </summary>
    public static IModeSwitchProvider? Select(ControlBackend preference, IEnumerable<IModeSwitchProvider> providers)
    {
        var list = providers.ToList();
        IModeSwitchProvider? Of(ControlBackend k) => list.FirstOrDefault(p => p.Kind == k);
        return preference switch
        {
            ControlBackend.MagicAap => Of(ControlBackend.MagicAap) is { Status.Ready: true } m ? m : null,
            ControlBackend.WslBridge => Of(ControlBackend.WslBridge) is { Status.Ready: true } w ? w : null,
            _ => Of(ControlBackend.MagicAap) is { Status.Ready: true } a ? a
               : Of(ControlBackend.WslBridge) is { Status.Ready: true } b ? b : null,
        };
    }

    /// <summary>Why nothing was selected, for the UI.</summary>
    public static string Explain(ControlBackend preference, IEnumerable<IModeSwitchProvider> providers)
    {
        var list = providers.ToList();
        string S(ControlBackend k) => list.FirstOrDefault(p => p.Kind == k)?.Status.Message ?? "not available";
        return preference switch
        {
            ControlBackend.MagicAap => "MagicAAP: " + S(ControlBackend.MagicAap),
            ControlBackend.WslBridge => "WSL2 bridge: " + S(ControlBackend.WslBridge),
            _ => $"WSL2 bridge: {S(ControlBackend.WslBridge)}  ·  MagicAAP: {S(ControlBackend.MagicAap)}",
        };
    }
}
