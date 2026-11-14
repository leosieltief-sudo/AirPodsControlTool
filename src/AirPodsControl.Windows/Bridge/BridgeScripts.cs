using System.Reflection;

namespace AirPodsControl.Windows.Bridge;

/// <summary>The Linux-side scripts embedded in this assembly and copied to /opt/airpods inside the distribution.
/// Copying needs no administrator rights, so updated scripts are pushed automatically (no wizard re-run).</summary>
public static class BridgeScripts
{
    public static readonly (string Resource, string Target)[] Files =
    [
        ("bridge/aap_bridge.py", "/opt/airpods/aap_bridge.py"),
        ("bridge/aapd.py", "/opt/airpods/aapd.py"),
        ("bridge/bridge.sh", "/opt/airpods/bridge.sh"),
    ];

    public static string Read(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidOperationException("Missing resource " + name);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>Writes all scripts into the distribution. Returns an error message or null.</summary>
    public static async Task<string?> CopyAsync(WslClient wsl, string distro, CancellationToken ct)
    {
        foreach (var (res, target) in Files)
        {
            var content = Read(res).Replace("\r\n", "\n");
            var w = await wsl.ExecAsync(distro, ["/bin/sh", "-c", $"mkdir -p /opt/airpods && cat > {target}.tmp && chmod 755 {target}.tmp && mv {target}.tmp {target}"],
                TimeSpan.FromSeconds(60), ct, stdin: content).ConfigureAwait(false);
            if (!w.Success) return $"Copying {target} failed: {w.FirstErrorLine}";
        }
        return null;
    }
}
