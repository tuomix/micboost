using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using MicBoost.Core.Settings;

namespace MicBoost.App.Services;

/// <summary>System-wide hotkeys via RegisterHotKey on the main window handle.</summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0xB000;

    public HotkeyManager(HwndSource source)
    {
        _source = source;
        _source.AddHook(WndProc);
    }

    /// <summary>Registers a hotkey. Returns an error message, or null on success (or when the gesture is empty).</summary>
    public string? Register(string gestureText, Action action)
    {
        if (string.IsNullOrWhiteSpace(gestureText)) return null;
        if (!HotkeyGesture.TryParse(gestureText, out var gesture))
            return $"\"{gestureText}\" is not a valid hotkey.";

        Key key;
        try
        {
            key = (Key)new KeyConverter().ConvertFromInvariantString(gesture.Key)!;
        }
        catch (Exception)
        {
            return $"Unknown key \"{gesture.Key}\".";
        }

        uint mods = MOD_NOREPEAT;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt)) mods |= MOD_ALT;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Ctrl)) mods |= MOD_CONTROL;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift)) mods |= MOD_SHIFT;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Win)) mods |= MOD_WIN;

        int id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, mods, (uint)KeyInterop.VirtualKeyFromKey(key)))
            return $"{gesture} is already used by another app.";
        _actions[id] = action;
        return null;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
    }

    /// <summary>Builds a gesture string from a key press in a hotkey text box (null for modifier-only presses).</summary>
    public static string? GestureFromKeyEvent(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.None or Key.ImeProcessed or Key.DeadCharProcessed)
            return null;
        var mods = HotkeyModifiers.None;
        var m = Keyboard.Modifiers;
        if (m.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Ctrl;
        if (m.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (m.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (m.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Win;
        return new HotkeyGesture(mods, new KeyConverter().ConvertToInvariantString(key) ?? key.ToString()).ToString();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            action();
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
