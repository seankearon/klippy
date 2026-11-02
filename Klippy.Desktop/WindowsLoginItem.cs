using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security;
using Klippy.Services;
using Microsoft.Win32;

namespace Klippy.Desktop;

/// <summary>
/// Start at login as Windows keeps it for an app installed per user: a value under
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>, holding the command line to run. That is the list Task
/// Manager's Startup apps and Settings' Startup page show, so Klippy appears there by name and can be switched off
/// from either; and per user, so it asks for no administrator's rights.
///
/// Switched off there, Windows leaves the Run value alone and records the choice beside it, under
/// <c>StartupApproved</c> — which is read here as off, since that is what it means at the next login. Turning it on
/// from Klippy clears that record as well as writing the value: it is the user asking again, in the other place.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsLoginItem(string processPath) : ILoginItem
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public LoginRegistration? Read()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (LoginItem.WindowsProgram(run?.GetValue(LoginItem.WindowsValueName) as string) is not { } program)
                return null;

            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            if (LoginItem.IsDisabledByWindows(approved?.GetValue(LoginItem.WindowsValueName) as byte[]))
                return null;

            return new LoginRegistration(program, LoginItem.SameProgram(program, processPath));
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public void Register()
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true))
            run.SetValue(LoginItem.WindowsValueName, LoginItem.WindowsCommand(processPath), RegistryValueKind.String);
        ForgetApproval();
    }

    public void Unregister()
    {
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            run?.DeleteValue(LoginItem.WindowsValueName, throwOnMissingValue: false);
        ForgetApproval();
    }

    /// <summary>
    /// Drops Task Manager's record of the entry, so a value written now starts enabled — and one taken away leaves
    /// nothing of itself behind.
    /// </summary>
    private static void ForgetApproval()
    {
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(LoginItem.WindowsValueName, throwOnMissingValue: false);
    }
}
