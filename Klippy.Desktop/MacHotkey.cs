using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Klippy.Services;

namespace Klippy.Desktop;

/// <summary>
/// macOS global hotkey via Carbon's RegisterEventHotKey.
///
/// Carbon is ancient but this specific API is still the standard way to take a system-wide
/// hotkey, and — unlike CGEventTap or NSEvent global monitors — it needs no Accessibility
/// permission, so the app works the moment it launches. Handlers are installed on the
/// application event target, which Avalonia's own run loop pumps for us.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacHotkey : IGlobalHotkey
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";

    // Carbon modifier masks (Events.h)
    private const uint CmdKey = 0x0100, ShiftKey = 0x0200, OptionKey = 0x0800, ControlKey = 0x1000;
    private const uint EventClassKeyboard = 0x6B657962;  // 'keyb'
    private const uint EventHotKeyPressed = 5;

    private readonly IntPtr _hotkeyRef;
    private readonly IntPtr _handlerRef;
    private readonly Action _onPressed;
    private readonly EventHandlerProc _proc; // rooted: Carbon holds a raw pointer

    public bool IsRegistered => _hotkeyRef != IntPtr.Zero;

    private MacHotkey(IntPtr hotkeyRef, IntPtr handlerRef, EventHandlerProc proc, Action onPressed)
    {
        _hotkeyRef = hotkeyRef;
        _handlerRef = handlerRef;
        _proc = proc;
        _onPressed = onPressed;
    }

    public static MacHotkey? Create(HotkeySpec spec, Action onPressed)
    {
        if (!HotkeySpec.MacKeyCodes.TryGetValue(spec.Key, out var keyCode)) return null;

        MacHotkey? instance = null;
        EventHandlerProc proc = (_, _, _) =>
        {
            instance?.Invoke();
            return 0; // noErr
        };

        var spec32 = new EventTypeSpec { eventClass = EventClassKeyboard, eventKind = EventHotKeyPressed };
        if (InstallEventHandler(GetApplicationEventTarget(), proc, 1, ref spec32, IntPtr.Zero, out var handlerRef) != 0)
            return null;

        var id = new EventHotKeyID { signature = 0x4B4C5059 /* 'KLPY' */, id = 1 };
        if (RegisterEventHotKey(keyCode, ToCarbonModifiers(spec.Modifiers), id,
                                GetApplicationEventTarget(), 0, out var hotkeyRef) != 0)
        {
            RemoveEventHandler(handlerRef);
            return null; // combination already claimed
        }

        instance = new MacHotkey(hotkeyRef, handlerRef, proc, onPressed);
        return instance;
    }

    private void Invoke() => _onPressed();

    private static uint ToCarbonModifiers(HotkeyModifiers m)
    {
        uint result = 0;
        if (m.HasFlag(HotkeyModifiers.Alt)) result |= OptionKey;
        if (m.HasFlag(HotkeyModifiers.Control)) result |= ControlKey;
        if (m.HasFlag(HotkeyModifiers.Shift)) result |= ShiftKey;
        if (m.HasFlag(HotkeyModifiers.Meta)) result |= CmdKey;
        return result;
    }

    public void Dispose()
    {
        if (_hotkeyRef != IntPtr.Zero) UnregisterEventHotKey(_hotkeyRef);
        if (_handlerRef != IntPtr.Zero) RemoveEventHandler(_handlerRef);
    }

    // ---- interop ----

    private delegate int EventHandlerProc(IntPtr callRef, IntPtr eventRef, IntPtr userData);

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec
    {
        public uint eventClass;
        public uint eventKind;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyID
    {
        public uint signature;
        public uint id;
    }

    [DllImport(Carbon)] private static extern IntPtr GetApplicationEventTarget();

    [DllImport(Carbon)]
    private static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyID id,
        IntPtr target, uint options, out IntPtr outRef);

    [DllImport(Carbon)] private static extern int UnregisterEventHotKey(IntPtr hotKeyRef);

    [DllImport(Carbon)]
    private static extern int InstallEventHandler(IntPtr target, EventHandlerProc handler,
        uint numTypes, ref EventTypeSpec typeList, IntPtr userData, out IntPtr outRef);

    [DllImport(Carbon)] private static extern int RemoveEventHandler(IntPtr handlerRef);
}
