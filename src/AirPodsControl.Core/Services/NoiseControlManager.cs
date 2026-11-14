using AirPodsControl.Core.Models;
using AirPodsControl.Core.Protocol;

namespace AirPodsControl.Core.Services;

public enum NoiseControlOutcome
{
    /// <summary>AirPods echoed the requested mode back.</summary>
    Confirmed,
    /// <summary>AirPods answered, but with a different mode (e.g. Off not allowed by device settings).</summary>
    DeviceReportedDifferentMode,
    /// <summary>Command was written, but no answer arrived in time. State is unknown.</summary>
    NoResponse,
    /// <summary>WSL bridge: command delivered on an acknowledged AAP channel; the AirPods apply it but send no echo to a
    /// host without an audio connection (AirPods Pro 3, verified by ear on the test PC). Shown as "sent", not "confirmed".</summary>
    Sent,
    NotSupportedByModel,
    NoControlChannel,
    /// <summary>Sending failed: the channel could not be opened or broke while writing. Nothing was delivered.</summary>
    TransportError,
    /// <summary>Not attempted: another operation was running, or a safety check (input devices, ambiguous adapter)
    /// stopped it before Bluetooth was touched.</summary>
    NotStarted,
}

public sealed record NoiseControlResult(NoiseControlOutcome Outcome, ListeningMode Requested, ListeningMode? Reported, string Message)
{
    public bool Success => Outcome is NoiseControlOutcome.Confirmed or NoiseControlOutcome.Sent;
    /// <summary>The AirPods themselves reported the requested mode.</summary>
    public bool Confirmed => Outcome == NoiseControlOutcome.Confirmed;
}

/// <summary>
/// Switches listening modes and tracks the mode the AirPods actually report.
/// <see cref="CurrentMode"/> is only ever set from data received from the device.
/// </summary>
public sealed class NoiseControlManager
{
    private readonly ILog _log;
    private AirPodsProtocolService? _protocol;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public NoiseControlManager(ILog? log = null) => _log = log ?? NullLog.Instance;

    public TimeSpan ConfirmationTimeout { get; set; } = TimeSpan.FromSeconds(2.5);

    /// <summary>When true, "Off" on models that need it first sends 0x34 "Allow Off Option = enabled" to the AirPods.</summary>
    public bool AutoEnableOffOption { get; set; } = true;

    public AirPodsModelInfo? Model { get; set; }

    /// <summary>Last listening mode reported by the AirPods (null = unknown).</summary>
    public ListeningMode? CurrentMode { get; private set; }

    /// <summary>Last value of control 0x34 reported by the AirPods (null = unknown).</summary>
    public bool? OffOptionAllowed { get; private set; }

    public bool IsBusy { get; private set; }

    public event Action<ListeningMode?>? ModeChanged;

    public void Attach(AirPodsProtocolService? protocol)
    {
        if (_protocol is not null) _protocol.ControlReceived -= OnControl;
        _protocol = protocol;
        CurrentMode = null;
        OffOptionAllowed = null;
        if (protocol is not null)
        {
            protocol.ControlReceived += OnControl;
            if (protocol.ControlState.TryGetValue(ControlId.ListeningMode, out var c)) CurrentMode = c.AsListeningMode;
            if (protocol.ControlState.TryGetValue(ControlId.AllowOffOption, out var o)) OffOptionAllowed = o.Value1 == 0x01;
        }
        ModeChanged?.Invoke(CurrentMode);
    }

    private void OnControl(ControlCommandMessage c)
    {
        if (c.Identifier == ControlId.AllowOffOption)
        {
            OffOptionAllowed = c.Value1 == 0x01;
        }
        else if (c.Identifier == ControlId.ListeningMode && c.AsListeningMode is { } m && m != CurrentMode)
        {
            CurrentMode = m;
            _log.Info($"AirPods report listening mode: {m}");
            ModeChanged?.Invoke(m);
        }
    }

    public async Task<NoiseControlResult> SetModeAsync(ListeningMode mode, CancellationToken ct = default)
    {
        var p = _protocol;
        if (p is null || !p.IsOpen || !p.HandshakeDone)
            return new(NoiseControlOutcome.NoControlChannel, mode, CurrentMode,
                "No AAP control channel is open. See 'Control channel' status for the reason.");
        if (Model is { } m && !AirPodsCatalog.SupportsMode(m, mode))
            return new(NoiseControlOutcome.NotSupportedByModel, mode, CurrentMode, $"{m.Name} does not support {mode}.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        IsBusy = true;
        try
        {
            if (mode == ListeningMode.Off && AutoEnableOffOption && OffOptionAllowed != true &&
                Model?.OffMode is OffModeRequirement.RequiresAllowOffOption or OffModeRequirement.Unknown or null)
            {
                _log.Info("Off requested: enabling 'Allow Off Option' (0x34 = 01) on the AirPods first");
                var echo = await p.SendControlAndAwaitAsync(ControlId.AllowOffOption, AapPackets.SetAllowOffOption(true), ConfirmationTimeout, ct).ConfigureAwait(false);
                if (echo is not null) OffOptionAllowed = echo.Value1 == 0x01;
                else _log.Warn("No echo for Allow Off Option; trying Off anyway");
            }

            var reply = await p.SendControlAndAwaitAsync(ControlId.ListeningMode, AapPackets.SetListeningMode(mode), ConfirmationTimeout, ct).ConfigureAwait(false);
            if (reply is null)
                return new(NoiseControlOutcome.NoResponse, mode, CurrentMode,
                    "Command sent but the AirPods did not confirm it. The current mode is unknown.");

            var reported = reply.AsListeningMode;
            if (reported == mode)
                return new(NoiseControlOutcome.Confirmed, mode, reported, $"{Describe(mode)} confirmed by AirPods.");

            string hint = mode == ListeningMode.Off
                ? " Off is probably disabled on the AirPods (iPhone: Settings → AirPods → 'Off Listening Mode')."
                : "";
            return new(NoiseControlOutcome.DeviceReportedDifferentMode, mode, reported,
                $"AirPods stayed in {(reported is null ? "an unknown mode" : Describe(reported.Value))}.{hint}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(NoiseControlOutcome.TransportError, mode, CurrentMode, "Connection closed while waiting for the AirPods.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error("SetMode failed", ex);
            return new(NoiseControlOutcome.TransportError, mode, CurrentMode, "Bluetooth write failed: " + ex.Message);
        }
        finally
        {
            IsBusy = false;
            _gate.Release();
        }
    }

    public static string Describe(ListeningMode m) => m switch
    {
        ListeningMode.NoiseCancellation => "Noise Cancellation",
        ListeningMode.Transparency => "Transparency",
        ListeningMode.Adaptive => "Adaptive",
        ListeningMode.Off => "Off",
        _ => m.ToString(),
    };
}
