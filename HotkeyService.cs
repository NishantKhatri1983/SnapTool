using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SnapTool;

/// <summary>System-wide hotkeys via RegisterHotKey on a hidden message-only window.</summary>
public sealed class HotkeyService : IDisposable
{
    readonly HwndSource _source;
    readonly Dictionary<int, Action> _actions = new();
    int _nextId = 1;

    public HotkeyService()
    {
        var p = new HwndSourceParameters("SnapToolHotkeys") { WindowStyle = 0, ParentWindow = new IntPtr(-3) }; // HWND_MESSAGE
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    public bool Register(string gesture, Action action, out string error)
    {
        error = "";
        if (!TryParse(gesture, out uint mods, out uint vk))
        {
            error = $"Can't understand hotkey \"{gesture}\".";
            return false;
        }
        int id = _nextId++;
        if (!Native.RegisterHotKey(_source.Handle, id, mods | Native.MOD_NOREPEAT, vk))
        {
            int win32 = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            error = $"Hotkey {gesture} is already used by another app.";
            Log.Write($"RegisterHotKey failed: {gesture} (mods 0x{mods:X}, vk 0x{vk:X}, error {win32})");
            return false;
        }
        Log.Write($"Hotkey registered: {gesture} (mods 0x{mods:X}, vk 0x{vk:X})");
        _actions[id] = action;
        return true;
    }

    /// <summary>True if no other app holds this hotkey right now (registers it briefly to find out).</summary>
    public static bool IsAvailable(string gesture)
    {
        if (!TryParse(gesture, out uint mods, out uint vk)) return false;
        const int probeId = 0xBFFF;
        if (!Native.RegisterHotKey(IntPtr.Zero, probeId, mods | Native.MOD_NOREPEAT, vk)) return false;
        Native.UnregisterHotKey(IntPtr.Zero, probeId);
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) Native.UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            // Run outside the window procedure so modal capture UI doesn't nest inside it.
            Dispatcher.CurrentDispatcher.BeginInvoke(action, DispatcherPriority.Send);
        }
        return IntPtr.Zero;
    }

    /// <summary>Parses "Ctrl+Shift+S", "Alt+F1", "PrintScreen", "Win+Shift+2".</summary>
    public static bool TryParse(string gesture, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(gesture)) return false;
        Key key = Key.None;
        foreach (var raw in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= Native.MOD_CONTROL; break;
                case "shift": mods |= Native.MOD_SHIFT; break;
                case "alt": mods |= Native.MOD_ALT; break;
                case "win": case "windows": mods |= Native.MOD_WIN; break;
                case "prtsc": case "printscreen": case "print": key = Key.PrintScreen; break;
                default:
                    var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                    if (!Enum.TryParse(name, true, out key)) return false;
                    break;
            }
        }
        if (key == Key.None) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return vk != 0;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.Dispose();
    }
}
