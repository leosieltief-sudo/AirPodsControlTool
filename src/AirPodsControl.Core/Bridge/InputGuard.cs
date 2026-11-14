namespace AirPodsControl.Core.Bridge;

public enum InputTransport { Classic, LowEnergy }

/// <param name="Connected">Current connection state from Windows; null = could not be determined.</param>
/// <param name="OnTargetAdapter">The device's node hangs below the adapter that would be handed to WSL; null = unknown.</param>
public sealed record InputDeviceInfo(string Name, ulong Address, InputTransport Transport, string Evidence, bool? Connected, bool? OnTargetAdapter);

public enum InputGuardStatus
{
    /// <summary>Ownership was determined and no input device is connected through the adapter.</summary>
    Clear,
    /// <summary>A mouse/keyboard/HID device is connected through the adapter and would stop working.</summary>
    InputDevicesConnected,
    /// <summary>Could not tell which input devices are connected through the adapter. Not safe by default.</summary>
    Unknown,
}

public sealed record InputGuardReport(InputGuardStatus Status, IReadOnlyList<InputDeviceInfo> Devices, IReadOnlyList<string> Problems)
{
    public IEnumerable<InputDeviceInfo> Affected => Devices.Where(d => d.OnTargetAdapter != false && d.Connected != false);

    public string Explain() => Status switch
    {
        InputGuardStatus.Clear => "No Bluetooth mouse, keyboard or other input device is connected through this adapter.",
        InputGuardStatus.InputDevicesConnected =>
            "These Bluetooth input devices are connected through the adapter and will stop working while it is handed to WSL: " +
            string.Join(", ", Affected.Select(Describe)) + ".",
        _ => "Could not determine which Bluetooth input devices use this adapter" +
             (Problems.Count > 0 ? " (" + string.Join("; ", Problems) + ")" : "") +
             (Affected.Any() ? ". Possibly affected: " + string.Join(", ", Affected.Select(Describe)) : "") +
             ". The hand-over is not known to be safe for your mouse/keyboard.",
    };

    private static string Describe(InputDeviceInfo d) =>
        $"{d.Name} ({(d.Transport == InputTransport.Classic ? "Classic HID" : "BLE HID")}{(d.Connected is null ? ", connection state unknown" : "")})";
}

/// <summary>
/// Decides whether handing the adapter to WSL would cut off a mouse/keyboard. Paired-but-disconnected devices do not
/// block; connected ones on this adapter do; anything undetermined is reported as <see cref="InputGuardStatus.Unknown"/>
/// (never as safe).
/// </summary>
public static class InputGuardPolicy
{
    public static InputGuardReport Evaluate(IReadOnlyList<InputDeviceInfo> devices, IReadOnlyList<string> problems)
    {
        bool connectedHere = devices.Any(d => d.Connected == true && d.OnTargetAdapter == true);
        bool undetermined = problems.Count > 0
            || devices.Any(d => d.OnTargetAdapter != false && d.Connected is null)
            || devices.Any(d => d.Connected == true && d.OnTargetAdapter is null);
        var status = connectedHere ? InputGuardStatus.InputDevicesConnected
            : undetermined ? InputGuardStatus.Unknown
            : InputGuardStatus.Clear;
        return new InputGuardReport(status, devices, problems);
    }

    /// <summary>Only an explicit user decision (dialog answer, dedicated CLI flag) may override a non-clear report.</summary>
    public static bool MayProceed(InputGuardReport report, bool explicitUserOverride) =>
        report.Status == InputGuardStatus.Clear || explicitUserOverride;
}
