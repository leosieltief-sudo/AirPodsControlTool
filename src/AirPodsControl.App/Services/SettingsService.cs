using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace AirPodsControl.App.Services;

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }
    public bool StartHidden { get; set; }
    /// <summary>Send "Allow Off Option" (control 0x34) to the AirPods when Off is requested on models that need it.</summary>
    public bool AutoEnableOffOption { get; set; } = true;
    public bool HotkeysEnabled { get; set; } = true;
    public string HotkeyNoiseCancellation { get; set; } = "Ctrl+Alt+1";
    public string HotkeyOff { get; set; } = "Ctrl+Alt+2";
    public string HotkeyTransparency { get; set; } = "Ctrl+Alt+3";
    public bool ShowNotifications { get; set; } = true;
    public bool ReduceMotion { get; set; }
    /// <summary>Bluetooth address of the preferred AirPods; null = automatic.</summary>
    public ulong? PreferredDevice { get; set; }
}

/// <summary>Stores settings as JSON in %APPDATA%\AirPodsControl. Nothing is sent anywhere.</summary>
public sealed class SettingsService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "AirPodsControl";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AirPodsControl");
    public static string LogDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirPodsControl", "logs");
    private static string FilePath => Path.Combine(DataDir, "settings.json");

    public AppSettings Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { Current = new(); }
        // the registry is the source of truth for autostart (user may have removed it in Task Manager)
        Current.StartWithWindows = IsAutostartEnabled();
    }

    public void Save(AppSettings s)
    {
        Current = s;
        Directory.CreateDirectory(DataDir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
        File.Move(tmp, FilePath, overwrite: true);
        SetAutostart(s.StartWithWindows);
    }

    public static bool IsAutostartEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunValue) is string;
    }

    public static void SetAutostart(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown process path");
            k.SetValue(RunValue, $"\"{exe}\" --background");
        }
        else if (k.GetValue(RunValue) is not null) k.DeleteValue(RunValue);
    }
}
