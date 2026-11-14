using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AirPodsControl.Core.Protocol;
using AirPodsControl.Core.Services;

namespace AirPodsControl.Core.Bridge;

/// <summary>One USB device as reported by usbipd-win.</summary>
public sealed record UsbipdDevice(string? BusId, string? VidPid, string? InstanceId, string Description, bool IsShared, bool IsAttached)
{
    public bool Matches(string vidPid) => string.Equals(VidPid, vidPid, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Parsers for `usbipd state` (JSON) and `usbipd list` (text), both from usbipd-win 5.x.</summary>
public static partial class UsbipdParser
{
    [GeneratedRegex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidPidInInstanceId();

    public static string? VidPidFromInstanceId(string? instanceId)
    {
        if (instanceId is null) return null;
        var m = VidPidInInstanceId().Match(instanceId);
        return m.Success ? $"{m.Groups[1].Value}:{m.Groups[2].Value}".ToLowerInvariant() : null;
    }

    /// <summary>
    /// `usbipd state`: {"Devices":[{"BusId":"1-14","InstanceId":"USB\\VID_..","Description":"..","PersistedGuid":"..|null",
    /// "ClientIPAddress":"..|null", ...}]}. Shared ⇔ PersistedGuid set; attached ⇔ ClientIPAddress set.
    /// Property names are matched case-insensitively (serializer casing not documented).
    /// </summary>
    public static IReadOnlyList<UsbipdDevice> ParseState(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!TryGet(root, "Devices", out var devices) || devices.ValueKind != JsonValueKind.Array) return [];
        var list = new List<UsbipdDevice>();
        foreach (var d in devices.EnumerateArray())
        {
            string? Str(string n) => TryGet(d, n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var instance = Str("InstanceId");
            list.Add(new UsbipdDevice(
                BusId: Str("BusId"),
                VidPid: VidPidFromInstanceId(instance),
                InstanceId: instance,
                Description: Str("Description") ?? "",
                IsShared: Str("PersistedGuid") is { Length: > 0 },
                IsAttached: Str("ClientIPAddress") is { Length: > 0 }));
        }
        return list;
    }

    [GeneratedRegex(@"^(?<bus>\d+-\d+(?:\.\d+)*)\s+(?<vp>[0-9a-fA-F]{4}:[0-9a-fA-F]{4})\s+(?<desc>.*?)\s{2,}(?<state>Not shared|Shared \(forced\)|Shared|Attached|Allowed|Incompatible hub)\s*$")]
    private static partial Regex ListRow();

    /// <summary>`usbipd list` "Connected:" section (fallback when `state` is unavailable).</summary>
    public static IReadOnlyList<UsbipdDevice> ParseList(string text)
    {
        var list = new List<UsbipdDevice>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("Persisted:", StringComparison.OrdinalIgnoreCase)) break;
            var m = ListRow().Match(line);
            if (!m.Success) continue;
            var st = m.Groups["state"].Value;
            list.Add(new UsbipdDevice(m.Groups["bus"].Value, m.Groups["vp"].Value.ToLowerInvariant(), null, m.Groups["desc"].Value.Trim(),
                IsShared: st.StartsWith("Shared") || st == "Attached", IsAttached: st == "Attached"));
        }
        return list;
    }

    private static bool TryGet(JsonElement e, string name, out JsonElement value)
    {
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }
}

/// <summary>A BR/EDR link key exported from the Windows registry (BTHPORT\Parameters\Keys\&lt;adapter&gt;).</summary>
public sealed record LinkKeyEntry(ulong AdapterAddress, ulong DeviceAddress, byte[] Key)
{
    public string AdapterMac => BtAddress.Format(AdapterAddress);
    public string DeviceMac => BtAddress.Format(DeviceAddress);
    public string KeyHex => Convert.ToHexString(Key);
}

public static class BtAddress
{
    public static string Format(ulong a) => string.Join(":", Enumerable.Range(0, 6).Select(i => ((a >> (40 - 8 * i)) & 0xFF).ToString("X2")));

    public static bool TryParse(string s, out ulong value)
    {
        var hex = s.Replace(":", "").Replace("-", "");
        return ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value) && hex.Length == 12;
    }
}

/// <summary>
/// Parses `reg export` output of HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Keys.
/// Classic link keys are REG_BINARY values named after the device address under the adapter-address key:
///   [HKEY_LOCAL_MACHINE\...\Keys\aabbccddeeff]
///   "a1b2c3d4e5f6"=hex:12,34,...  (16 bytes)
/// Sub-keys (LE devices: LTK/IRK…) and non-16-byte values are ignored. The key bytes are used unchanged in
/// BlueZ's [LinkKey] Key= (same byte order, as done by dual-boot pairing tools).
/// </summary>
public static partial class WindowsLinkKeyParser
{
    [GeneratedRegex(@"^\[HKEY_LOCAL_MACHINE\\SYSTEM\\(?:CurrentControlSet|ControlSet\d{3})\\Services\\BTHPORT\\Parameters\\Keys\\(?<adapter>[0-9a-fA-F]{12})\]$", RegexOptions.IgnoreCase)]
    private static partial Regex AdapterHeader();

    [GeneratedRegex(@"^""(?<dev>[0-9a-fA-F]{12})""=hex:(?<bytes>[0-9a-fA-F,\\\s]*)$")]
    private static partial Regex KeyValueStart();

    public static IReadOnlyList<LinkKeyEntry> Parse(string regExport)
    {
        var result = new List<LinkKeyEntry>();
        ulong? adapter = null;
        var lines = regExport.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('['))
            {
                var h = AdapterHeader().Match(line);
                adapter = h.Success ? Convert.ToUInt64(h.Groups["adapter"].Value, 16) : null; // sub-keys reset the context
                continue;
            }
            if (adapter is null) continue;
            var m = KeyValueStart().Match(line);
            if (!m.Success) continue;
            var hex = m.Groups["bytes"].Value;
            while (hex.TrimEnd().EndsWith('\\') && i + 1 < lines.Length) hex = hex.TrimEnd().TrimEnd('\\') + lines[++i].Trim();
            var bytes = hex.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(b => Convert.ToByte(b, 16)).ToArray();
            if (bytes.Length != 16) continue;
            result.Add(new LinkKeyEntry(adapter.Value, Convert.ToUInt64(m.Groups["dev"].Value, 16), bytes));
        }
        return result;
    }
}

/// <summary>Final "RESULT {json}" line printed by bridge.sh / aap_bridge.py.</summary>
public sealed record BridgeResult(
    bool Ok, string Outcome, int? Requested, int? Reported, int? ModeBefore, int? Mode, bool? AllowOff,
    IReadOnlyList<BridgeBattery> Battery, string? Error, string? Address, string Raw)
{
    public ListeningMode? ReportedMode => ToMode(Reported) ?? (Outcome == "read_only" ? ToMode(Mode) : null);
    public ListeningMode? CurrentMode => ToMode(Reported) ?? ToMode(Mode);

    private static ListeningMode? ToMode(int? v) => v is >= 1 and <= 4 ? (ListeningMode)v.Value : null;
}

public sealed record BridgeBattery(string Component, int? Level, bool Charging);

public static class BridgeResultParser
{
    public const string Prefix = "RESULT ";

    /// <summary>Finds the last RESULT line in the output. Returns null when the script printed none (crash / missing python).</summary>
    public static BridgeResult? Parse(string output)
    {
        var line = output.Replace("\r", "").Split('\n').LastOrDefault(l => l.StartsWith(Prefix, StringComparison.Ordinal));
        if (line is null) return null;
        var json = line[Prefix.Length..];
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            int? I(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var x) ? x : null;
            string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            bool? B(string n) => r.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
            var batt = new List<BridgeBattery>();
            if (r.TryGetProperty("battery", out var ba) && ba.ValueKind == JsonValueKind.Array)
                foreach (var e in ba.EnumerateArray())
                {
                    var comp = e.TryGetProperty("component", out var c) ? c.GetString() ?? "" : "";
                    int? lvl = e.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : null;
                    bool chg = e.TryGetProperty("charging", out var ch) && ch.ValueKind == JsonValueKind.True;
                    if (lvl is < 0 or > 100) lvl = null;
                    batt.Add(new BridgeBattery(comp, lvl, chg));
                }
            return new BridgeResult(B("ok") ?? false, S("outcome") ?? "unknown", I("requested"), I("reported"), I("mode_before"),
                I("mode"), B("allow_off"), batt, S("error"), S("address"), json);
        }
        catch (JsonException)
        {
            return new BridgeResult(false, "bad_result", null, null, null, null, null, [], "Unreadable bridge output: " + json, null, json);
        }
    }

    /// <summary>Maps a bridge result to the same outcome model the MagicAAP path uses. "confirmed" (echo) and "sent"
    /// (delivered on an acknowledged channel, no echo without A2DP) are success; "sent" is never shown as confirmed.</summary>
    public static NoiseControlResult ToNoiseControlResult(BridgeResult? r, ListeningMode requested)
    {
        if (r is null)
            return new(NoiseControlOutcome.TransportError, requested, null, "The WSL bridge produced no result (see log).");
        var reported = r.ReportedMode;
        return r.Outcome switch
        {
            "confirmed" when reported == requested =>
                new(NoiseControlOutcome.Confirmed, requested, reported, $"{NoiseControlManager.Describe(requested)} confirmed by AirPods (WSL bridge)."),
            "different" or "confirmed" =>
                new(NoiseControlOutcome.DeviceReportedDifferentMode, requested, reported,
                    $"AirPods stayed in {(reported is { } m ? NoiseControlManager.Describe(m) : "an unknown mode")}." +
                    (requested == ListeningMode.Off ? " Off is probably disabled on the AirPods (iPhone: Settings → AirPods → Off Listening Mode)." : "")),
            "sent" =>
                new(NoiseControlOutcome.Sent, requested, null,
                    $"{NoiseControlManager.Describe(requested)} sent over the WSL bridge (the AirPods apply it but do not confirm it to this host)."),
            "no_response" =>
                new(NoiseControlOutcome.NoResponse, requested, null, "Command sent over the WSL bridge, but the AirPods did not confirm it."),
            "adapter_not_ready" =>
                new(NoiseControlOutcome.TransportError, requested, null, "The Bluetooth adapter did not start inside WSL: " + (r.Error ?? "hci0 missing") +
                    ". Your adapter/WSL kernel combination may not be supported (see Driverless setup → Test)."),
            "a2dp_failed" =>
                new(NoiseControlOutcome.TransportError, requested, null, "Could not open the audio (A2DP) connection from WSL, which the AirPods require before they accept noise control: " +
                    (r.Error ?? "unknown") + ". Take the AirPods out of the case and try again."),
            "no_key" =>
                new(NoiseControlOutcome.TransportError, requested, null, "The WSL bridge has no pairing key for this adapter: " + (r.Error ?? "unknown") +
                    ". Run Driverless setup again to copy it."),
            "connect_failed" =>
                new(NoiseControlOutcome.TransportError, requested, null, "Could not open the AAP channel from WSL: " + (r.Error ?? "unknown") +
                    ". Check that the pairing key was copied (Driverless setup) and the AirPods are out of the case."),
            _ => new(NoiseControlOutcome.TransportError, requested, null, $"WSL bridge error ({r.Outcome}): {r.Error ?? "unknown"}"),
        };
    }
}
