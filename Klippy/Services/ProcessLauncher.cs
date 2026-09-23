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

        if (ExecutionPolicy.Resolve(plan, ExecutionPolicy.CurrentPlatform, IsExecutable) is not { } command)
            return ExecutionResult.Failed("Klippy cannot run that on this platform.");

        // Before the start, and waited for: the whole point is that what runs is the
        // version in the repository rather than the one left on disk last week.
        //
        // Ahead of the missing-target check below, not after it: a script added in a
        // commit this checkout has not seen yet is exactly what a pull is for, and
        // refusing it first would mean the pull could never fetch it.
        var pulled = PullFirst(plan);

        if (Missing(plan) is { } absent)
            return ExecutionResult.Failed(Note(absent, pulled));

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
            return new ExecutionResult(true, Note(plan.Description, pulled));
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
    /// A pull that worked says nothing: it is what was asked for, and the message already
    /// names what is happening. One that did not has to say so, or a stale script runs
    /// looking exactly like a fresh one — and a target still missing after a failed pull
    /// is a failure whose reason is the pull.
    /// </summary>
    private static string Note(string message, string? pulled) =>
        pulled is null ? message : $"{message} — {pulled}";

    /// <summary>
    /// Brings the target's repository up to date, when the plan asks for it and the target
    /// lives in one. Returns null when there was nothing to do or it worked, and otherwise
    /// what went wrong, in the few words the toast has room for.
    ///
    /// Failing does not stop the run. The pull is there to make what starts current, not
    /// to be a gate on starting at all: a laptop off the network would otherwise be a
    /// laptop that cannot run its own scripts.
    /// </summary>
    private static string? PullFirst(ExecutionPlan plan)
    {
        if (!ExecutionPolicy.PullsFirst(plan)) return null;

        // Path.IsPathRooted, not the policy's own reading of what a path looks like: this
        // runs on the machine it is about, so the platform's answer is the right one. A
        // bare "notepad.exe" for Windows to find on PATH names no folder to pull in, and
        // must not be taken as one relative to wherever Klippy happens to be running.
        if (!Path.IsPathRooted(plan.Target)) return null;
        if (DirectoryOf(plan.Target) is not { } directory) return null;
        if (RepositoryOf(directory) is null) return null; // not a checkout: nothing to pull

        var command = ExecutionPolicy.PullCommand(directory);
        var start = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in command.Arguments)
            start.ArgumentList.Add(argument);

        // Nothing is watching a console here, so a git that stops to ask for a password
        // would hang until the timeout below. Told not to ask, it fails in the moment and
        // says so instead.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";

        try
        {
            using var git = Process.Start(start);
            if (git is null) return "git pull could not start";

            if (!git.WaitForExit(PullTimeoutMs))
            {
                git.Kill(entireProcessTree: true);
                return "git pull timed out";
            }

            return git.ExitCode == 0 ? null : $"git pull failed ({git.ExitCode})";
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException
                                      or UnauthorizedAccessException or PlatformNotSupportedException
                                      or NotSupportedException)
        {
            // Typically git is not installed, which is worth saying once rather than
            // silently running the stale copy.
            return $"git pull failed: {e.Message}";
        }
    }

    /// <summary>How long to wait for a pull before giving up on it and running anyway.</summary>
    private const int PullTimeoutMs = 30_000;

    /// <summary>
    /// The git working tree <paramref name="directory"/> sits in, or null if it is not in
    /// one. Walks up, because a script normally lives in a folder of a repository rather
    /// than at its root — <c>.git</c> is a directory in a checkout and a file in a
    /// worktree or submodule, so either counts.
    /// </summary>
    public static string? RepositoryOf(string? directory)
    {
        try
        {
            for (var current = directory; !string.IsNullOrEmpty(current);
                 current = Path.GetDirectoryName(current))
            {
                var git = Path.Combine(current, ".git");
                if (Directory.Exists(git) || File.Exists(git)) return current;
            }
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException
                                      or UnauthorizedAccessException)
        {
            // An unreadable parent is not a repository we can pull in either.
        }

        return null;
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

        // The program inside one is a file.
        ExecutionKind.Application when ExecutionPolicy.BundleOf(plan.Target) is not null
                                      && !File.Exists(plan.Target) =>
            $"Application not found: {plan.Target}",

        // Windows would answer a missing document with a shell dialog, and xdg-open with a
        // line on a console nobody is reading. Say it in the toast instead.
        ExecutionKind.Document when !File.Exists(plan.Target) =>
            $"File not found: {plan.Target}",

        ExecutionKind.Folder when !Directory.Exists(plan.Target) =>
            $"Folder not found: {plan.Target}",

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
