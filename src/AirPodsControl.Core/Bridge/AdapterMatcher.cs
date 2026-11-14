namespace AirPodsControl.Core.Bridge;

/// <summary>The exact USB device that will be handed to WSL, and how it relates to the radio Windows is using.</summary>
/// <param name="WindowsRadioDeviceId">Interface path of Windows' Bluetooth adapter, when it is this USB device.</param>
/// <param name="RadioBelongsToAdapter">Windows' active Bluetooth radio is this USB device: it must be switched off
/// (only this one) before usbipd can take it.</param>
public sealed record AdapterTarget(string BusId, string UsbInstanceId, string? WindowsRadioDeviceId, bool RadioBelongsToAdapter, string Description);

public sealed record AdapterResolution(AdapterTarget? Target, string? Error)
{
    public bool Ok => Target is not null;
    public static AdapterResolution Fail(string error) => new(null, error);
}

/// <summary>Windows' Bluetooth adapter (WinRT BluetoothAdapter.DeviceId) and the device-tree chain of its radio node
/// (the node itself, its parent, grandparent…), e.g. USB\VID_13D3&amp;PID_3622&amp;MI_00\… → USB\VID_13D3&amp;PID_3622\….</summary>
public sealed record WindowsRadioInfo(string DeviceId, IReadOnlyList<string> InstanceChain);

/// <summary>
/// Maps the configured adapter to one usbipd device and to Windows' radio without guessing: never by VID:PID alone
/// when several identical adapters are present; stops (before anything is switched off) when the mapping is unclear.
/// </summary>
public static class AdapterMatcher
{
    /// <summary>"\\?\USB#VID_13D3&amp;PID_3622&amp;MI_00#7&amp;3b2f8856&amp;0&amp;0000#{guid}" → "USB\VID_13D3&amp;PID_3622&amp;MI_00\7&amp;3b2f8856&amp;0&amp;0000".</summary>
    public static string? InterfacePathToInstanceId(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        int brace = p.LastIndexOf("#{", StringComparison.Ordinal);
        if (brace > 0) p = p[..brace];
        var parts = p.Split('#');
        return parts.Length >= 3 ? string.Join('\\', parts) : null;
    }

    public static AdapterResolution Resolve(string vidPid, string? configuredUsbInstanceId, IReadOnlyList<UsbipdDevice> devices, WindowsRadioInfo? windowsRadio)
    {
        var candidates = devices.Where(d => d.Matches(vidPid) && d.BusId is not null && d.InstanceId is not null).ToList();
        if (candidates.Count == 0) return AdapterResolution.Fail($"Bluetooth adapter {vidPid} not found by usbipd.");

        var chain = windowsRadio?.InstanceChain ?? [];
        bool InChain(UsbipdDevice d) => chain.Any(c => string.Equals(c, d.InstanceId, StringComparison.OrdinalIgnoreCase));
        if (windowsRadio is not null && chain.Count == 0)
            return AdapterResolution.Fail("Could not map Windows' Bluetooth radio to a USB device; stopped before switching anything off.");

        UsbipdDevice? target;
        if (!string.IsNullOrEmpty(configuredUsbInstanceId))
        {
            target = candidates.FirstOrDefault(d => string.Equals(d.InstanceId, configuredUsbInstanceId, StringComparison.OrdinalIgnoreCase));
            if (target is null)
                return AdapterResolution.Fail($"The configured adapter ({configuredUsbInstanceId}) is not connected; usbipd sees {candidates.Count} other {vidPid} device(s). Run Driverless setup again.");
        }
        else if (candidates.Count == 1) target = candidates[0];
        else
        {
            // several identical adapters: only the one that is Windows' radio can be identified without guessing
            var inChain = candidates.Where(InChain).ToList();
            if (inChain.Count != 1)
                return AdapterResolution.Fail($"{candidates.Count} adapters {vidPid} are connected and it is unclear which one to hand over; stopped before switching anything off. Run Driverless setup again to pick one.");
            target = inChain[0];
        }

        if (!target.IsShared) return AdapterResolution.Fail($"Adapter {vidPid} ({target.BusId}) is not shared with usbipd (bind). Run Driverless setup again.");
        bool belongs = windowsRadio is not null && InChain(target);
        return new(new AdapterTarget(target.BusId!, target.InstanceId!, belongs ? windowsRadio!.DeviceId : null, belongs,
            $"{target.Description} ({target.BusId}, {target.InstanceId})" +
            (windowsRadio is null ? "; Windows has no active Bluetooth adapter" : belongs ? "; it is Windows' active radio" : "; Windows uses a different adapter (left untouched)")), null);
    }
}
