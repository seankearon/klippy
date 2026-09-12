using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Klippy.Services;

/// <summary>How an execution went, in the one line the toast has room for.</summary>
public sealed record ExecutionResult(bool Started, string Message)
{
    public static ExecutionResult Failed(string message) => new(false, message);
}

/// <summary>
/// Starts what <see cref="ExecutionPolicy"/> decided on — a link, a script or an
/// application — the half of execution that touches the machine, kept apart from the
/// rules so those stay testable.
///
/// Arguments are always handed over as a list rather than as one command line, so a
/// macro's value is an argument and can never become a second command. The exception
/// that proves it is a <c>.bat</c> file, which cmd.exe re-parses after .NET has quoted
/// it: <see cref="ExecutionPolicy"/> refuses those arguments outright instead.
/// </summary>
public static class ProcessLauncher
{
    /// <summary>Runs the plan off the UI thread; never throws.</summary>
    public static Task<ExecutionResult> RunAsync(ExecutionPlan plan) => Task.Run(() => Run(plan));

    public static ExecutionResult Run(ExecutionPlan plan)
    {
        if (plan.Kind == ExecutionKind.None)
            return ExecutionResult.Failed(plan.Problem);

        if (Missing(plan) is { } absent)
            return ExecutionResult.Failed(absent);

        if (ExecutionPolicy.Resolve(plan, ExecutionPolicy.CurrentPlatform, IsExecutable) is not { } command)
            return ExecutionResult.Failed("Klippy cannot run that on this platform.");

        var start = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = command.UseShellExecute,
        };
        foreach (var argument in command.Arguments)
            start.ArgumentList.Add(argument);

        // A script is written expecting to run from its own folder, and an application
        // started from its folder is what double-clicking one does.
        if (plan.Kind is ExecutionKind.Script or ExecutionKind.Application
            && DirectoryOf(plan.Target) is { } directory)
            start.WorkingDirectory = directory;

        try
        {
            using var process = Process.Start(start);
            return new ExecutionResult(true, plan.Description);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException
                                      or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Typically the interpreter itself is missing — no pwsh on a Mac, no bash on
            // Windows — so name what could not be started rather than only why.
            return ExecutionResult.Failed($"Could not run {command.FileName}: {e.Message}");
        }
    }

    /// <summary>
    /// Why the target cannot be there to start, or null if there is no reason to think
    /// it is missing. Only asked where the answer would otherwise arrive as a console
    /// window that closes before it can be read: an interpreter complaining about a
    /// script it was not given, or `open` complaining about a bundle. An .exe is started
    /// directly, so a missing one comes back as a Win32 error below — and may anyway be
    /// a bare name for Windows to find on PATH rather than a path to look up here.
    /// </summary>
    private static string? Missing(ExecutionPlan plan) => plan.Kind switch
    {
        ExecutionKind.Script when !File.Exists(plan.Target) =>
            $"Script not found: {plan.Target}",

        // A macOS bundle is a directory, so that is what answers for it.
        ExecutionKind.Application when ExecutionPolicy.ApplicationExtension(plan.Target) == ".app"
                                      && !Directory.Exists(plan.Target) =>
            $"Application not found: {plan.Target}",

        _ => null,
    };

    private static bool IsExecutable(string path)
    {
        // There is no execute bit on Windows, and nothing there asks: a .sh goes to bash.
        if (OperatingSystem.IsWindows()) return false;

        try
        {
            return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or PlatformNotSupportedException or ArgumentException)
        {
            return false; // treat it as a plain file and hand it to sh
        }
    }

    private static string? DirectoryOf(string path)
    {
        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } directory
                   && Directory.Exists(directory)
                ? directory
                : null;
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException
                                      or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
