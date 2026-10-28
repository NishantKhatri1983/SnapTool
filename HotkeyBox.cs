using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SnapTool;

/// <summary>A read-only box that records a key combination when the user presses it ("Ctrl+Shift+S").</summary>
internal sealed class HotkeyBox : TextBox
{
    string _gesture = "";

    public event Action? GestureChanged;

    public string Gesture
    {
        get => _gesture;
        set
        {
            _gesture = value?.Trim() ?? "";
            Text = _gesture.Length == 0 ? "(none)" : _gesture;
        }
    }

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        Cursor = Cursors.Hand;
        Width = 230;
        Padding = new Thickness(8, 4, 8, 4);
        VerticalContentAlignment = VerticalAlignment.Center;
        FontWeight = FontWeights.SemiBold;
        ContextMenu = null;
        ToolTip = "Click, then press the key combination. Backspace clears it.";
        GotKeyboardFocus += (_, _) =>
        {
            Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF6, 0xFF));
            Text = "Press keys…";
        };
        LostKeyboardFocus += (_, _) =>
        {
            ClearValue(BackgroundProperty);
            Gesture = _gesture;
        };
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (key == Key.Tab && mods is ModifierKeys.None or ModifierKeys.Shift) return; // keep normal focus movement
        e.Handled = true;
        if (key == Key.Escape && mods == ModifierKeys.None)
        {
            MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            return;
        }
        if ((key == Key.Back || key == Key.Delete) && mods == ModifierKeys.None)
        {
            Gesture = "";
            Text = "(none)";
            GestureChanged?.Invoke();
            return;
        }
        Record(key);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        // Print Screen only produces a key-up in WPF.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Snapshot)
        {
            e.Handled = true;
            Record(key);
        }
        else if (IsModifier(key) && IsKeyboardFocused && Keyboard.Modifiers == ModifierKeys.None)
        {
            Text = _gesture.Length == 0 ? "Press keys…" : _gesture;
        }
    }

    void Record(Key key)
    {
        var parts = new List<string>();
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) parts.Add("Win");
        if (IsModifier(key))
        {
            Text = string.Join("+", parts) + "+…"; // waiting for the main key
            return;
        }
        parts.Add(KeyName(key));
        Gesture = string.Join("+", parts);
        GestureChanged?.Invoke();
    }

    static bool IsModifier(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    static string KeyName(Key k) => k switch
    {
        Key.Snapshot => "PrintScreen",
        >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
        _ => k.ToString(),
    };

    /// <summary>Keys that are fine as a hotkey without Ctrl/Alt/Shift/Win.</summary>
    public static bool WorksAlone(string gesture)
    {
        var key = gesture.Trim();
        return key.Equals("PrintScreen", StringComparison.OrdinalIgnoreCase) || key.Equals("Pause", StringComparison.OrdinalIgnoreCase)
            || key.Equals("Scroll", StringComparison.OrdinalIgnoreCase) || key.Equals("Insert", StringComparison.OrdinalIgnoreCase)
            || (key.Length >= 2 && (key[0] == 'F' || key[0] == 'f') && int.TryParse(key[1..], out int n) && n is >= 1 and <= 24);
    }
}
