using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Klippy.Services;

namespace Klippy.Desktop;

/// <summary>
/// Win32 global hotkey via RegisterHotKey.
///
/// RegisterHotKey delivers WM_HOTKEY to the thread that registered it, so this owns a
/// dedicated thread running a message-only window and its own pump. Piggy-backing on
/// Avalonia's UI thread would mean intercepting its window procedure; a message-only
/// window keeps the two completely independent, and tearing it down is just PostQuitMessage.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsHotkey : IGlobalHotkey
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 0xC1A9;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;
    private const uint MOD_NOREPEAT = 0x4000; // holding the key must not re-fire
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly Thread _thread;
    private readonly Action _onPressed;
    private uint _threadId;
    private volatile bool _registered;
    private WndProcDelegate? _wndProc; // rooted: the OS holds a raw pointer to this

    public bool IsRegistered => _registered;

    private WindowsHotkey(HotkeySpec spec, Action onPressed)
    {
        _onPressed = onPressed;
        using var ready = new ManualResetEventSlim(false);

        _thread = new Thread(() => Pump(spec, ready))
        {
            IsBackground = true,
            Name = "Klippy global hotkey",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    public static WindowsHotkey? Create(HotkeySpec spec, Action onPressed)
    {
        var hotkey = new WindowsHotkey(spec, onPressed);
        return hotkey.IsRegistered ? hotkey : null;
    }

    private void Pump(HotkeySpec spec, ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        IntPtr hwnd = IntPtr.Zero;

        try
        {
            _wndProc = WndProc;
            var cls = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                lpszClassName = "KlippyHotkeyWindow+" + Guid.NewGuid().ToString("N"),
                hInstance = GetModuleHandle(null),
            };
            if (RegisterClass(ref cls) == 0) return;

            hwnd = CreateWindowEx(0, cls.lpszClassName, "", 0, 0, 0, 0, 0,
                                  HWND_MESSAGE, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) return;

            if (!RegisterHotKey(hwnd, HOTKEY_ID, ToWin32Modifiers(spec.Modifiers) | MOD_NOREPEAT,
                                HotkeySpec.VirtualKeys[spec.Key]))
                return; // already taken by another app

            _registered = true;
        }
        finally
        {
            ready.Set(); // unblock the constructor whether or not we succeeded
        }

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_HOTKEY && msg.wParam.ToInt32() == HOTKEY_ID)
                _onPressed();
        }

        UnregisterHotKey(hwnd, HOTKEY_ID);
        DestroyWindow(hwnd);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) =>
        DefWindowProc(hWnd, msg, wParam, lParam);

    private static uint ToWin32Modifiers(HotkeyModifiers m)
    {
        uint result = 0;
        if (m.HasFlag(HotkeyModifiers.Alt)) result |= MOD_ALT;
        if (m.HasFlag(HotkeyModifiers.Control)) result |= MOD_CONTROL;
        if (m.HasFlag(HotkeyModifiers.Shift)) result |= MOD_SHIFT;
        if (m.HasFlag(HotkeyModifiers.Meta)) result |= MOD_WIN;
        return result;
    }

    public void Dispose()
    {
        if (_threadId != 0)
            PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    // ---- interop ----

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName,
        uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
