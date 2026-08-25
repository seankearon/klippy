using System;
using Klippy.Services;

namespace Klippy.Desktop;

/// <summary>A registered system-wide hotkey. Disposing unregisters it.</summary>
public interface IGlobalHotkey : IDisposable
{
    bool IsRegistered { get; }
}

public static class GlobalHotkey
{
    /// <summary>
    /// Registers <paramref name="spec"/> system-wide and invokes <paramref name="onPressed"/>
    /// when it fires. Returns null where the platform is unsupported or the combination is
    /// already claimed by another application — callers should treat that as "no hotkey"
    /// rather than a fatal error.
    /// </summary>
    public static IGlobalHotkey? TryRegister(HotkeySpec spec, Action onPressed)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsHotkey.Create(spec, onPressed);
            if (OperatingSystem.IsMacOS()) return MacHotkey.Create(spec, onPressed);
        }
        catch (Exception)
        {
            // Missing native entry points, sandboxing, etc. — degrade to no hotkey.
        }
        return null;
    }
}
