using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Klippy.Services;

namespace Klippy.Desktop;

/// <summary>
/// Runs what <see cref="LaunchPolicy"/> recognised in an unmatched search: a URL, a folder,
/// a program, or one of the OS controls.
///
/// Every failure is allowed to throw. The view model catches it and shows the message where
/// the offer was, which is the only way the user finds out that the shell has no handler for
/// a file type or that hibernation is turned off on this machine.
/// </summary>
internal static class SystemLauncher
{
    public static void Run(LaunchTarget target)
    {
        switch (target.Kind)
        {
            case LaunchKind.Url:
            case LaunchKind.Folder:
            case LaunchKind.File:
                Shell(target.Target);
                break;

            case LaunchKind.System:
                RunSystemAction(target.Action);
                break;
        }
    }

    /// <summary>
    /// Hands the path or URL to the OS with the shell verb it would use for a double-click:
    /// Explorer or Finder for a folder, the default browser for a URL, and for a file
    /// whatever owns that extension.
    ///
    /// Deliberately not "run this script with an interpreter". A <c>.ps1</c> opens in the
    /// editor on a stock Windows box because that is what Windows does with it, and a
    /// launcher that quietly did something more than the double-click would be a launcher
    /// that executes text the user only meant to look at.
    /// </summary>
    private static void Shell(string target)
    {
        // UseShellExecute is what picks the handler. .NET routes it through ShellExecuteEx on
        // Windows and through open/xdg-open elsewhere, so one path serves all three desktops.
        using var started = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    private static void RunSystemAction(SystemAction action)
    {
        if (OperatingSystem.IsWindows()) RunOnWindows(action);
        else if (OperatingSystem.IsMacOS()) RunOnMac(action);
        else RunOnLinux(action);
    }

    [SupportedOSPlatform("windows")]
    private static void RunOnWindows(SystemAction action)
    {
        switch (action)
        {
            case SystemAction.Lock:
                if (!LockWorkStation()) throw new InvalidOperationException("Windows would not lock the workstation.");
                break;

            // The documented wrinkle: with hibernation enabled, asking for sleep gets you
            // hibernation anyway — the call is a request, and the power policy decides. The
            // alternative is turning hibernation off behind the user's back, which is not
            // Klippy's to do.
            case SystemAction.Sleep:
                Suspend(hibernate: false);
                break;

            case SystemAction.Hibernate:
                Suspend(hibernate: true);
                break;

            // shutdown.exe rather than ExitWindowsEx, which needs SeShutdownPrivilege
            // enabled on the token first; /t 0 skips the minute-long warning.
            case SystemAction.Restart:
                Start("shutdown.exe", "/r", "/t", "0");
                break;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Suspend(bool hibernate)
    {
        if (!SetSuspendState(hibernate, bForce: false, bWakeupEventsDisabled: false))
            throw new InvalidOperationException(hibernate
                ? "Windows refused to hibernate — it may be turned off (powercfg /hibernate on)."
                : "Windows refused to sleep.");
    }

    private static void RunOnMac(SystemAction action)
    {
        switch (action)
        {
            // The lock the Apple menu offers: a fast-user-switch suspend, not a display
            // sleep that only locks if the "require password" preference happens to be set.
            case SystemAction.Lock:
                Start("/System/Library/CoreServices/Menu Extras/User.menu/Contents/Resources/CGSession", "-suspend");
                break;

            case SystemAction.Sleep:
                Start("pmset", "sleepnow");
                break;

            // macOS has no user-facing hibernate: it is a sleep mode (pmset hibernatemode),
            // not a command. Saying so beats pretending the key did nothing.
            case SystemAction.Hibernate:
                throw new PlatformNotSupportedException(
                    "macOS has no separate hibernate — use sleep, or set pmset hibernatemode.");

            case SystemAction.Restart:
                Start("osascript", "-e", "tell application \"System Events\" to restart");
                break;
        }
    }

    private static void RunOnLinux(SystemAction action) => Start(
        action == SystemAction.Lock ? "loginctl" : "systemctl",
        action switch
        {
            SystemAction.Lock => "lock-session",
            SystemAction.Sleep => "suspend",
            SystemAction.Hibernate => "hibernate",
            _ => "reboot",
        });

    /// <summary>Runs a command with no console window of its own, and does not wait for it.</summary>
    private static void Start(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var started = Process.Start(info);
    }

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [SupportedOSPlatform("windows")]
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.Bool)] bool hibernate,
        [MarshalAs(UnmanagedType.Bool)] bool bForce,
        [MarshalAs(UnmanagedType.Bool)] bool bWakeupEventsDisabled);
}
