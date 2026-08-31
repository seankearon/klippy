using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Klippy.Desktop;

/// <summary>
/// A hidden Win32 window with its own thread and message pump.
///
/// Two features need one: the global hotkey (<c>RegisterHotKey</c> delivers WM_HOTKEY to
/// the registering thread) and clipboard capture (<c>AddClipboardFormatListener</c>
/// delivers WM_CLIPBOARDUPDATE to a window). Both want a message loop that is completely
/// independent of Avalonia's — piggy-backing on the UI thread would mean intercepting its
/// window procedure, and a hidden HWND_MESSAGE window keeps the two from knowing about
/// each other at all.
///
/// <paramref name="attach"/> and <paramref name="detach"/> both run on the pump thread,
/// which is what the Win32 APIs involved require: the thread that registers is the thread
/// that must unregister.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MessageOnlyWindow : IDisposable
{
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private const uint WM_QUIT = 0x0012;

    private readonly Thread _thread;
    private readonly Func<IntPtr, bool> _attach;
    private readonly Action<uint, IntPtr, IntPtr> _onMessage;
    private readonly Action<IntPtr>? _detach;
    private uint _threadId;
    private volatile bool _running;
    private volatile nint _handle;
    private WndProcDelegate? _wndProc; // rooted: the OS holds a raw pointer to this

    /// <summary>True once the window exists and <c>attach</c> succeeded.</summary>
    public bool IsRunning => _running;

    /// <summary>
    /// The window itself, or zero before it exists. Writing to the clipboard needs an
    /// owner window in this process, or the write cannot be told apart from anyone else's.
    /// </summary>
    public IntPtr Handle => _handle;

    /// <param name="name">Window class prefix, for debuggability only.</param>
    /// <param name="attach">Registers interest on the pump thread; false aborts the window.</param>
    /// <param name="onMessage">Called on the pump thread for each message retrieved.</param>
    /// <param name="detach">Unregisters on the pump thread, before the window is destroyed.</param>
    public MessageOnlyWindow(
        string name,
        Func<IntPtr, bool> attach,
        Action<uint, IntPtr, IntPtr> onMessage,
        Action<IntPtr>? detach = null)
    {
        _attach = attach;
        _onMessage = onMessage;
        _detach = detach;

        using var ready = new ManualResetEventSlim(false);

        _thread = new Thread(() => Pump(name, ready))
        {
            IsBackground = true,
            Name = $"Klippy {name}",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    private void Pump(string name, ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        IntPtr hwnd = IntPtr.Zero;

        try
        {
            _wndProc = WndProc;
            var cls = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                // Unique per instance: a class name may only be registered once per process.
                lpszClassName = $"Klippy{name}Window+{Guid.NewGuid():N}",
                hInstance = GetModuleHandle(null),
            };
            if (RegisterClass(ref cls) == 0) return;

            hwnd = CreateWindowEx(0, cls.lpszClassName, "", 0, 0, 0, 0, 0,
                                  HWND_MESSAGE, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) return;

            if (!_attach(hwnd)) return;

            _handle = hwnd;
            _running = true;
        }
        finally
        {
            ready.Set(); // unblock the constructor whether or not we succeeded
        }

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            _onMessage(msg.message, msg.wParam, msg.lParam);

        _detach?.Invoke(hwnd);
        _handle = IntPtr.Zero;
        DestroyWindow(hwnd);
        _running = false;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) =>
        DefWindowProc(hWnd, msg, wParam, lParam);

    public void Dispose()
    {
        if (_threadId != 0)
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
