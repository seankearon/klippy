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
/// Starts what <see cref="ExecutionPolicy"/> decided on — the half of execution that
/// touches the machine, kept apart from the rules so those stay testable.
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

        // A missing script deserves saying so plainly, rather than as whatever the
        // interpreter would have complained about in a window that closes instantly.
        if (plan.Kind == ExecutionKind.Script && !File.Exists(plan.Target))
            return ExecutionResult.Failed($"Script not found: {plan.Target}");

        if (ExecutionPolicy.Resolve(plan, ExecutionPolicy.CurrentPlatform, IsExecutable) is not { } command)
            return ExecutionResult.Failed("Klippy cannot run that on this platform.");

        var start = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = command.UseShellExecute,
        };
        foreach (var argument in command.Arguments)
            start.ArgumentList.Add(argument);

        // Scripts are written expecting to run from their own folder.
        if (plan.Kind == ExecutionKind.Script && DirectoryOf(plan.Target) is { } directory)
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
