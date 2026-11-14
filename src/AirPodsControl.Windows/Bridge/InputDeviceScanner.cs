using System.Text.RegularExpressions;
using AirPodsControl.Core.Bridge;
using AirPodsControl.Core.Services;
using Windows.Devices.Bluetooth;

namespace AirPodsControl.Windows.Bridge;

/// <summary>
/// Finds Bluetooth input devices (Classic HID, BLE HID-over-GATT) that are connected through a given adapter.
///  * Candidates and adapter membership come from the device tree: HID service nodes (BTHENUM\{00001124…},
///    BTHLEDEVICE\{00001812…}) and paired Classic devices whose Class of Device (Windows' pairing record in the registry)
///    is "peripheral" — below the adapter's USB node or elsewhere (another adapter). Paired devices keep these nodes, so
///    the tree does not tell "connected".
///  * "Connected right now" comes from WinRT BluetoothDevice / BluetoothLEDevice.ConnectionStatus per candidate (no radio
///    traffic). Enumerating association endpoints instead took 30 s per query on the test PC — too slow before a hotkey.
/// Whatever cannot be determined is reported as such (InputGuardStatus.Unknown), never as safe.
/// </summary>
public static partial class InputDeviceScanner
{
    private const string LeHid = "00001812-0000-1000-8000-00805F9B34FB";

    [GeneratedRegex(@"^BTHENUM\\\{(?<svc>[0-9A-F\-]{36})\}.*[&_](?<mac>[0-9A-F]{12})_C[0-9A-F]+$", RegexOptions.IgnoreCase)]
    private static partial Regex ClassicServiceNode();

    [GeneratedRegex(@"^BTHLE\\DEV_(?<mac>[0-9A-F]{12})\\", RegexOptions.IgnoreCase)]
    private static partial Regex LeDeviceNode();

    public static async Task<InputGuardReport> ScanAsync(AdapterTarget target, ulong? exclude, ILog log)
    {
        var problems = new List<string>();
        var found = new Dictionary<(ulong, InputTransport), (string evidence, bool? onTarget)>();

        var below = Native.GetDescendants(target.UsbInstanceId);
        if (below is null) problems.Add("the adapter's device node is not present");
        else
        {
            var set = new HashSet<string>(below, StringComparer.OrdinalIgnoreCase);
            foreach (var id in below) Classify(id, true, found);
            foreach (var id in Native.GetPresentDeviceIds("BTHENUM").Concat(Native.GetPresentDeviceIds("BTHLEDEVICE")))
                if (!set.Contains(id)) Classify(id, false, found);   // the same kinds of nodes elsewhere: another adapter
        }

        var devices = new List<InputDeviceInfo>();
        foreach (var ((addr, transport), (evidence, onTarget)) in found)
        {
            if (addr == exclude) continue;
            var (name, connected, error) = await StateAsync(addr, transport).ConfigureAwait(false);
            if (error is not null) log.Debug($"input scan: {Format(addr)} state unknown: {error}");
            devices.Add(new InputDeviceInfo(name ?? Format(addr), addr, transport, evidence, connected, onTarget));
        }
        return InputGuardPolicy.Evaluate(devices, problems);
    }

    private static async Task<(string? name, bool? connected, string? error)> StateAsync(ulong addr, InputTransport transport)
    {
        try
        {
            // fresh pool thread + time limit: a hanging WinRT call must not delay a hotkey forever (result: unknown, not safe)
            var t = Task.Run<(string?, bool?)>(async () =>
            {
                if (transport == InputTransport.Classic)
                {
                    using var d = await BluetoothDevice.FromBluetoothAddressAsync(addr);
                    return d is null ? (null, null) : (d.Name, d.ConnectionStatus == BluetoothConnectionStatus.Connected);
                }
                using var le = await BluetoothLEDevice.FromBluetoothAddressAsync(addr);
                return le is null ? (null, null) : (le.Name, le.ConnectionStatus == BluetoothConnectionStatus.Connected);
            });
            if (await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(4))).ConfigureAwait(false) != t) return (null, null, "timed out");
            var (n, c) = await t.ConfigureAwait(false);
            return (string.IsNullOrWhiteSpace(n) ? null : n, c, c is null ? "Windows does not know the device" : null);
        }
        catch (Exception ex) { return (null, null, ex.Message); }
    }

    private static void Classify(string id, bool onTarget, Dictionary<(ulong, InputTransport), (string, bool?)> found)
    {
        if (ClassicServiceNode().Match(id) is { Success: true } m)
        {
            ulong addr = Convert.ToUInt64(m.Groups["mac"].Value, 16);
            if (string.Equals(m.Groups["svc"].Value, "00001124-0000-1000-8000-00805F9B34FB", StringComparison.OrdinalIgnoreCase))
                Put(found, (addr, InputTransport.Classic), "Classic HID service node", onTarget);
            else if (ClassOfDeviceMajor(addr) == (int)BluetoothMajorClass.Peripheral)
                Put(found, (addr, InputTransport.Classic), "paired Classic device with Class of Device 'peripheral'", onTarget);
        }
        else if (id.StartsWith(@"BTHLEDEVICE\{" + LeHid + "}", StringComparison.OrdinalIgnoreCase))
        {
            // the address is in the BTHLE\DEV_xxxxxxxxxxxx ancestor
            var le = Native.GetAncestorChain(id).Select(a => LeDeviceNode().Match(a)).FirstOrDefault(x => x.Success);
            if (le is not null)
                Put(found, (Convert.ToUInt64(le.Groups["mac"].Value, 16), InputTransport.LowEnergy), "HID-over-GATT service node", onTarget);
        }
    }

    /// <summary>Major device class from Windows' pairing record (readable without admin rights), or null.</summary>
    private static int? ClassOfDeviceMajor(ulong addr)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\" + addr.ToString("x12"));
            return k?.GetValue("COD") is int cod ? (cod >> 8) & 0x1F : null;
        }
        catch { return null; }
    }

    private static void Put(Dictionary<(ulong, InputTransport), (string, bool?)> found, (ulong, InputTransport) key, string evidence, bool onTarget)
    {
        // "below the target" wins over "elsewhere" if a device somehow shows up twice
        if (!found.TryGetValue(key, out var cur) || (onTarget && cur.Item2 != true)) found[key] = (evidence, onTarget);
    }

    private static string Format(ulong a) => string.Join(":", Enumerable.Range(0, 6).Select(i => ((a >> (40 - 8 * i)) & 0xFF).ToString("X2")));
}
