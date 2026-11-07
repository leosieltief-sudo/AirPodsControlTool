using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace AirPodsControl.App.Services;

public sealed record HotkeyRegistration(string Name, string Gesture, bool Success, string? Error);

/// <summary>
/// System-wide hotkeys via RegisterHotKey. Windows refuses a combination that another application already owns
/// (ERROR_HOTKEY_ALREADY_REGISTERED), which is how conflicts are detected and reported.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0x5100;

    public HotkeyService()
    {
        _source = new HwndSource(new HwndSourceParameters("AirPodsControlHotkeys") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ });
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var a)) { a(); handled = true; }
        return IntPtr.Zero;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    public HotkeyRegistration Register(string name, string gesture, Action action)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return new(name, gesture, false, "not set");
        if (!TryParse(gesture, out uint mods, out uint vk, out string? perr)) return new(name, gesture, false, perr);
        int id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, mods | MOD_NOREPEAT, vk))
        {
            int err = Marshal.GetLastWin32Error();
            string msg = err == 1409 ? "already used by another application" : new Win32Exception(err).Message;
            return new(name, gesture, false, msg);
        }
        _actions[id] = action;
        return new(name, gesture, true, null);
    }

    public static bool TryParse(string gesture, out uint mods, out uint vk, out string? error)
    {
        mods = 0; vk = 0; error = null;
        Key key = Key.None;
        foreach (var raw in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win" or "windows": mods |= MOD_WIN; break;
                default:
                    var token = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                    if (!Enum.TryParse(token, true, out key)) { error = $"unknown key '{raw}'"; return false; }
                    break;
            }
        }
        if (key == Key.None) { error = "no key"; return false; }
        if (mods == 0) { error = "needs at least one modifier"; return false; }
        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return true;
    }

    /// <summary>Formats a key event from the settings dialog as "Ctrl+Alt+X".</summary>
    public static string? Format(ModifierKeys m, Key k)
    {
        if (k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
            return null;
        var parts = new List<string>();
        if (m.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (m.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (m.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (m.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        var name = k is >= Key.D0 and <= Key.D9 ? ((int)(k - Key.D0)).ToString() : k.ToString();
        parts.Add(name);
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.Dispose();
    }
}
