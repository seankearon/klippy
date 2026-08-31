using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Klippy.Services;

namespace Klippy.Desktop;

/// <summary>
/// Win32 global hotkey via RegisterHotKey.
///
/// RegisterHotKey delivers WM_HOTKEY to the thread that registered it, so registration
/// and the message loop both live on <see cref="MessageOnlyWindow"/>'s pump thread.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsHotkey : IGlobalHotkey
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 0xC1A9;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;
    private const uint MOD_NOREPEAT = 0x4000; // holding the key must not re-fire

    private readonly MessageOnlyWindow _window;

    public bool IsRegistered => _window.IsRunning;

    private WindowsHotkey(HotkeySpec spec, Action onPressed)
    {
        _window = new MessageOnlyWindow(
            "Hotkey",
            attach: hwnd => RegisterHotKey(hwnd, HOTKEY_ID,
                ToWin32Modifiers(spec.Modifiers) | MOD_NOREPEAT, HotkeySpec.VirtualKeys[spec.Key]),
            onMessage: (msg, wParam, _) =>
            {
                if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
                    onPressed();
            },
            detach: hwnd => UnregisterHotKey(hwnd, HOTKEY_ID));
    }

    /// <summary>Null when the combination is already claimed by another application.</summary>
    public static WindowsHotkey? Create(HotkeySpec spec, Action onPressed)
    {
        var hotkey = new WindowsHotkey(spec, onPressed);
        if (hotkey.IsRegistered) return hotkey;

        hotkey.Dispose();
        return null;
    }

    private static uint ToWin32Modifiers(HotkeyModifiers m)
    {
        uint result = 0;
        if (m.HasFlag(HotkeyModifiers.Alt)) result |= MOD_ALT;
        if (m.HasFlag(HotkeyModifiers.Control)) result |= MOD_CONTROL;
        if (m.HasFlag(HotkeyModifiers.Shift)) result |= MOD_SHIFT;
        if (m.HasFlag(HotkeyModifiers.Meta)) result |= MOD_WIN;
        return result;
    }

    public void Dispose() => _window.Dispose();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
