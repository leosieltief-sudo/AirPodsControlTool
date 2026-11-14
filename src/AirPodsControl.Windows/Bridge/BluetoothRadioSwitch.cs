using AirPodsControl.Core.Bridge;
using AirPodsControl.Core.Services;
using Windows.Devices.Bluetooth;
using Windows.Devices.Radios;

namespace AirPodsControl.Windows.Bridge;

/// <summary>
/// Turns one specific Windows Bluetooth radio off/on through the user-level WinRT API (same switch as Settings / Action
/// Center, no administrator rights). The radio is addressed by its adapter's interface path, never "the first Bluetooth
/// radio": with several adapters only the one being handed over is touched. Needed because Windows' stack refuses to
/// release the USB adapter to usbipd while its radio is on ("Device busy (exported)" on the test PC).
/// </summary>
public sealed class BluetoothRadioSwitch(ILog log)
{
    /// <summary>Windows' active Bluetooth adapter and the device-tree chain of its radio node, or null if there is none.</summary>
    public static async Task<WindowsRadioInfo?> GetWindowsRadioAsync()
    {
        var a = await BluetoothAdapter.GetDefaultAsync();
        if (a is null) return null;
        var inst = AdapterMatcher.InterfacePathToInstanceId(a.DeviceId);
        return new WindowsRadioInfo(a.DeviceId, inst is null ? [] : Native.GetAncestorChain(inst));
    }

    private static async Task<Radio?> RadioForAsync(string adapterDeviceId)
    {
        var a = await BluetoothAdapter.FromIdAsync(adapterDeviceId);
        return a is null ? null : await a.GetRadioAsync();
    }

    public async Task<RadioState?> GetStateAsync(string adapterDeviceId)
    {
        try { return (await RadioForAsync(adapterDeviceId))?.State; }
        catch (Exception ex) { log.Warn("radio query failed: " + ex.Message); return null; }
    }

    /// <summary>Sets the radio of this adapter. Returns null on success, else an error message.</summary>
    public async Task<string?> SetAsync(string adapterDeviceId, RadioState state)
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return "Windows denied access to the Bluetooth radio switch.";
            var radio = await RadioForAsync(adapterDeviceId);
            if (radio is null) return "The Bluetooth radio of this adapter was not found.";
            var r = await radio.SetStateAsync(state);
            log.Info($"Bluetooth radio {radio.Name} → {state}: {r}");
            return r == RadioAccessStatus.Allowed ? null : $"Bluetooth radio could not be switched {state} ({r}).";
        }
        catch (Exception ex) { return "Bluetooth radio switch failed: " + ex.Message; }
    }

    /// <summary>After usbipd detach the adapter re-enumerates under the same interface path; waits for it, then switches it on.</summary>
    public async Task<string?> RestoreOnAsync(string adapterDeviceId, TimeSpan timeout, CancellationToken ct)
    {
        var end = DateTime.UtcNow + timeout;
        string? last = "Bluetooth radio did not reappear.";
        while (DateTime.UtcNow < end)
        {
            try
            {
                var radio = await RadioForAsync(adapterDeviceId);
                if (radio is not null)
                {
                    if (radio.State == RadioState.On) return null;
                    last = await SetAsync(adapterDeviceId, RadioState.On);
                    if (last is null) return null;
                }
            }
            catch (Exception ex) { last = ex.Message; }
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        return last;
    }
}
