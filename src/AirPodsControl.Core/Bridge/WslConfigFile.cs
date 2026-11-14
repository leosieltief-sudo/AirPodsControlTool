using System.Text;
using System.Text.RegularExpressions;

namespace AirPodsControl.Core.Bridge;

public enum WslKernelSetting { Stock, Ours, Foreign }

/// <summary>
/// Minimal, lossless editor for %UserProfile%\.wslconfig: only the <c>kernel</c> / <c>kernelModules</c> keys of the
/// <c>[wsl2]</c> section are touched; every other line (comments, other sections, ordering) is preserved verbatim.
/// "Ours" means the value points inside the AirPods Control kernel directory.
/// </summary>
public static partial class WslConfigFile
{
    public const string Marker = "# AirPods Control: WSL kernel with MediaTek Bluetooth support (removed by 'Remove driverless mode')";
    private static readonly string[] Keys = ["kernel", "kernelModules"];

    [GeneratedRegex(@"^\s*\[(?<name>[^\]]+)\]\s*$")] private static partial Regex SectionRx();
    [GeneratedRegex(@"^\s*(?<key>[A-Za-z0-9_.]+)\s*=\s*(?<value>.*?)\s*$")] private static partial Regex KeyRx();

    /// <summary>.wslconfig paths use escaped backslashes: C:\\dir\\bzImage.</summary>
    public static string Escape(string path) => path.Replace("\\", "\\\\");

    public static string Unescape(string value)
    {
        var v = value.Trim();
        var hash = v.IndexOf(" #", StringComparison.Ordinal);
        if (hash >= 0) v = v[..hash].Trim();
        if (v.Length >= 2 && (v[0] == '"' && v[^1] == '"' || v[0] == '\'' && v[^1] == '\'')) v = v[1..^1];
        return v.Replace("\\\\", "\\");
    }

    public static WslKernelSetting GetKernel(string? text, string ourDir, out string? configuredPath)
    {
        configuredPath = null;
        if (string.IsNullOrEmpty(text)) return WslKernelSetting.Stock;
        string? section = null;
        foreach (var line in SplitLines(text))
        {
            var s = SectionRx().Match(line);
            if (s.Success) { section = s.Groups["name"].Value.Trim(); continue; }
            if (!IsWsl2(section)) continue;
            var k = KeyRx().Match(line);
            if (k.Success && k.Groups["key"].Value.Equals("kernel", StringComparison.OrdinalIgnoreCase))
                configuredPath = Unescape(k.Groups["value"].Value);
        }
        if (configuredPath is null) return WslKernelSetting.Stock;
        return IsUnder(configuredPath, ourDir) ? WslKernelSetting.Ours : WslKernelSetting.Foreign;
    }

    /// <summary>Points WSL at our kernel + modules. Throws if another custom kernel is configured (never overwritten).</summary>
    public static string SetKernel(string? text, string kernelPath, string modulesPath, string ourDir)
    {
        var state = GetKernel(text, ourDir, out var existing);
        if (state == WslKernelSetting.Foreign)
            throw new InvalidOperationException($".wslconfig already uses another custom kernel ({existing}).");

        var lines = string.IsNullOrEmpty(text) ? new List<string>() : SplitLines(text).ToList();
        var nl = text?.Contains("\r\n") == true || string.IsNullOrEmpty(text) ? "\r\n" : "\n";
        var ours = new[] { Marker, $"kernel={Escape(kernelPath)}", $"kernelModules={Escape(modulesPath)}" };

        lines = RemoveOurs(lines, ourDir);
        var header = lines.FindIndex(l => SectionRx().Match(l) is { Success: true } m && IsWsl2(m.Groups["name"].Value.Trim()));
        if (header < 0)
        {
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            if (lines.Count > 0) lines.Add("");
            lines.Add("[wsl2]");
            lines.AddRange(ours);
        }
        else lines.InsertRange(header + 1, ours);
        return string.Join(nl, lines) + nl;
    }

    /// <summary>Removes only our kernel/kernelModules lines and marker; drops a [wsl2] section left empty.</summary>
    public static string RemoveKernel(string text, string ourDir)
    {
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = RemoveOurs(SplitLines(text).ToList(), ourDir);
        // drop an empty [wsl2] header (only blank lines until the next section or EOF)
        for (int i = 0; i < lines.Count; i++)
        {
            if (SectionRx().Match(lines[i]) is not { Success: true } m || !IsWsl2(m.Groups["name"].Value.Trim())) continue;
            int j = i + 1;
            while (j < lines.Count && lines[j].Trim().Length == 0) j++;
            if (j == lines.Count || SectionRx().IsMatch(lines[j])) { lines.RemoveRange(i, j - i); i--; }
        }
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines.Count == 0 ? "" : string.Join(nl, lines) + nl;
    }

    private static List<string> RemoveOurs(List<string> lines, string ourDir)
    {
        var result = new List<string>(lines.Count);
        string? section = null;
        foreach (var line in lines)
        {
            var s = SectionRx().Match(line);
            if (s.Success) section = s.Groups["name"].Value.Trim();
            if (line.Trim() == Marker) continue;
            if (IsWsl2(section) && KeyRx().Match(line) is { Success: true } k
                && Keys.Contains(k.Groups["key"].Value, StringComparer.OrdinalIgnoreCase)
                && IsUnder(Unescape(k.Groups["value"].Value), ourDir)) continue;
            result.Add(line);
        }
        return result;
    }

    private static bool IsWsl2(string? section) => string.Equals(section, "wsl2", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string path, string dir)
    {
        static string N(string p) => p.Replace('/', '\\').TrimEnd('\\');
        var p = N(path); var d = N(dir);
        return p.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase) || p.Equals(d, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        var t = text.Replace("\r\n", "\n");
        if (t.EndsWith('\n')) t = t[..^1];
        return t.Split('\n');
    }
}

public static partial class MediaTekUsbIds
{
    /// <summary>MediaTek adapter? Exact btusb ID list first (IMC Networks 13d3, Foxconn 0489 … also sell Realtek/Intel
    /// modules, so their VID alone is not enough), then MediaTek's own VID 0e8d, then the driver's name/manufacturer.</summary>
    public static bool IsMediaTek(string vidPid, string? name, string? manufacturer) =>
        Known.Contains(vidPid) ||
        vidPid.StartsWith("0e8d:", StringComparison.OrdinalIgnoreCase) ||
        ($"{name} {manufacturer}").Contains("MediaTek", StringComparison.OrdinalIgnoreCase);
}
