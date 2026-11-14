using System.Text.Json;
using Microsoft.Win32;

namespace AirPodsControl.Windows.Bridge;

/// <summary>State of the driverless (WSL2) setup, stored in %LOCALAPPDATA%\AirPodsControl\bridge\bridge.json.
/// Contains no keys: pairing keys only ever live inside the WSL distribution.</summary>
public sealed class BridgeConfig
{
    /// <summary>2: A2DP (bluez-alsa) before AAP, ordered handshake. 3: persistent daemon aapd.py (management socket, 4: kernel event trace + read op; 5: configure op (Enable Off: 0x34 + 0x1A), control capture;
    /// no bluetoothd). Scripts newer than <see cref="MinReadyScriptVersion"/> are pushed automatically (no admin needed).</summary>
    public const int CurrentScriptVersion = 5;
    /// <summary>Oldest provisioned script set that can be upgraded in place (same packages, only the scripts changed).</summary>
    public const int MinReadyScriptVersion = 2;

    /// <summary>Use the persistent daemon (aapd.py) for mode switches; false = one bridge.sh run per command (old path).</summary>
    public bool UseDaemon { get; set; } = true;
    /// <summary>The daemon (and with it the WSL VM) exits after this many idle minutes; 0 = keep running.</summary>
    public double DaemonIdleMinutes { get; set; } = 15;
    /// <summary>After the adapter is back, ask Windows to open a connection to the AirPods instead of waiting for them.</summary>
    public bool ReconnectKick { get; set; } = true;

    public string Distro { get; set; } = "airpods-bridge";
    public bool Provisioned { get; set; }
    public int ScriptVersion { get; set; }
    public string? AdapterVidPid { get; set; }
    /// <summary>usbipd's instance id of the exact USB device (several identical adapters must never be told apart by VID:PID).</summary>
    public string? AdapterUsbInstanceId { get; set; }
    /// <summary>Interface path of the Windows adapter whose radio a running hand-over switched off (for recovery).</summary>
    public string? RadioDeviceId { get; set; }
    public string? AdapterMac { get; set; }
    public List<string> KeysInstalledFor { get; set; } = [];
    public bool? AdapterTestOk { get; set; }
    public string? AdapterTestMessage { get; set; }
    public DateTimeOffset? AdapterTestAt { get; set; }
    /// <summary>Seconds to wait for hci0 after attaching (firmware download can take longer on some adapters).</summary>
    public double HciTimeoutSeconds { get; set; } = 4;
    /// <summary>Set while a bridge session has switched the Windows Bluetooth radio off (restored on the next start if a session died).</summary>
    public bool RadioTurnedOff { get; set; }
    /// <summary>Last press-and-hold cycle this app wrote: what it sent and what came back. Not the AirPods' current state.</summary>
    public OffCycleRecord? LastOffCycle { get; set; }
    /// <summary>Last press-and-hold cycle the AirPods themselves reported (0x1A) in a bridge session, if they ever did.</summary>
    public OffCycleRecord? HoldCycleReported { get; set; }

    public static string Dir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirPodsControl", "bridge");
    public static string DistroDir => Path.Combine(Dir, "distro");
    private static string FilePath => Path.Combine(Dir, "bridge.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static BridgeConfig Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(FilePath)) ?? new(); }
        catch { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public static void Delete() { try { File.Delete(FilePath); } catch { } }

    public bool HasKeyFor(ulong address) => KeysInstalledFor.Contains(address.ToString("X12"), StringComparer.OrdinalIgnoreCase);
}

/// <summary>A press-and-hold cycle record: device address (12 hex digits), cycle mask, time, outcome / "Reported".</summary>
public sealed class OffCycleRecord
{
    public string Device { get; set; } = "";
    public byte Modes { get; set; }
    public DateTimeOffset At { get; set; }
    public string Status { get; set; } = "";
    public string? OpId { get; set; }
}

/// <summary>The USB Bluetooth radio Windows is using (needed to tell usbipd which device to hand over).</summary>
public sealed record BluetoothUsbAdapter(string InstanceId, string VidPid, string Name, string? Manufacturer)
{
    public bool IsMediaTek => Core.Bridge.MediaTekUsbIds.IsMediaTek(VidPid, Name, Manufacturer);

    private const string BluetoothClassGuid = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";

    /// <summary>Finds present USB device nodes of class "Bluetooth" that are radios (not the per-device BTHENUM children).</summary>
    public static IReadOnlyList<BluetoothUsbAdapter> FindAll()
    {
        var list = new List<BluetoothUsbAdapter>();
        foreach (var id in Native.GetPresentDeviceIds("USB"))
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + id);
                if (k is null) continue;
                // "Class" is not always written (absent on the Windows 11 26200 test PC); ClassGUID is.
                bool isBt = k.GetValue("Class") is string cls && cls.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase)
                    || k.GetValue("ClassGUID") is string g && g.Equals(BluetoothClassGuid, StringComparison.OrdinalIgnoreCase);
                if (!isBt) continue;
                var vp = Core.Bridge.UsbipdParser.VidPidFromInstanceId(id);
                if (vp is null) continue;
                string name = Clean(k.GetValue("FriendlyName") as string) ?? Clean(k.GetValue("DeviceDesc") as string) ?? "Bluetooth adapter";
                list.Add(new BluetoothUsbAdapter(id, vp, name, Clean(k.GetValue("Mfg") as string)));
            }
            catch { }
        }
        return list;
    }

    // "@oem12.inf,%mtk%;MediaTek Inc." → "MediaTek Inc."
    private static string? Clean(string? s) => s is null ? null : s.Contains(';') ? s[(s.LastIndexOf(';') + 1)..] : s;
}
