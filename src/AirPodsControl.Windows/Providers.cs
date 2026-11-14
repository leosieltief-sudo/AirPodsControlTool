using AirPodsControl.Core.Protocol;
using AirPodsControl.Core.Services;
using AirPodsControl.Windows.Bridge;

namespace AirPodsControl.Windows;

/// <summary>MagicAAP kernel-driver backend: the persistent L2CAP channel owned by <see cref="AirPodsController"/>.</summary>
public sealed class MagicAapProvider(NoiseControlManager noiseControl, Func<ProviderStatus> status) : IModeSwitchProvider
{
    public ControlBackend Kind => ControlBackend.MagicAap;
    public string DisplayName => "MagicAAP driver";
    public bool InterruptsAudio => false;
    public ProviderStatus Status => status();

    public Task<NoiseControlResult> SetModeAsync(ModeSwitchRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        noiseControl.AutoEnableOffOption = request.AutoEnableOffOption;
        return noiseControl.SetModeAsync(request.Mode, ct);
    }
}
