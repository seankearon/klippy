using System;
using System.IO;
using System.Threading.Tasks;
using Klippy.Models;
using Klippy.Services;
using Klippy.ViewModels;
using Xunit;

namespace Klippy.Tests;

/// <summary>
/// Bringing a script's folder up to date before running it: which plans ask for it, where
/// the pull happens, and how the preference reaches the launcher.
///
/// The pull itself starts a process, so it is tested the way the rest of
/// <see cref="ProcessLauncher"/> is — not at all. What is pinned down here is everything
/// that decides <em>whether</em> to run it, which is where a mistake would either pull in
/// the wrong folder or quietly never pull at all.
/// </summary>
public class PullFirstTests
{
    private static ExecutionPlan Plan(ExecutionKind kind, bool pullFirst = true) =>
        new() { Kind = kind, Target = "/repo/tools/deploy.sh", PullFirst = pullFirst };

    // ---- which plans pull ----

    [Theory]
    [InlineData(ExecutionKind.Script)]
    [InlineData(ExecutionKind.Application)]
    public void AScriptOrApplicationPullsFirst_BecauseItIsAFileKeptSomewhere(ExecutionKind kind)
    {
        Assert.True(ExecutionPolicy.PullsFirst(Plan(kind)));
    }

    [Theory]
    [InlineData(ExecutionKind.Url)]      // no working copy to bring up to date
    [InlineData(ExecutionKind.Folder)]   // being opened, not run
    [InlineData(ExecutionKind.System)]   // the machine has no checkout
    [InlineData(ExecutionKind.None)]
    public void NothingElseDoes(ExecutionKind kind)
    {
        Assert.False(ExecutionPolicy.PullsFirst(Plan(kind)));
    }

    [Fact]
    public void WithThePreferenceOff_EvenAScriptDoesNot()
    {
        Assert.False(ExecutionPolicy.PullsFirst(Plan(ExecutionKind.Script, pullFirst: false)));
    }

    [Fact]
    public void ThePullIsAnOrdinaryCommand_WithNoShellToReadIt()
    {
        // -C rather than a working directory, and arguments as arguments: the same rule
        // the rest of execution follows, so a path with a space in it stays one path.
        var command = ExecutionPolicy.PullCommand(@"C:\work\my scripts");

        Assert.Equal("git", command.FileName);
        Assert.Equal(["-C", @"C:\work\my scripts", "pull"], command.Arguments);
        Assert.False(command.UseShellExecute);
    }

    // ---- where the pull happens ----

    [Fact]
    public void TheRepositoryIsFoundFromAFolderInsideIt()
    {
        // A script lives in a folder of a checkout far more often than at its root, so the
        // walk up is the whole job.
        using var repo = new TempTree();
        repo.Directory(".git");
        var deep = repo.Directory("tools/deploy/nested");

        Assert.Equal(repo.Root, ProcessLauncher.RepositoryOf(deep));
        Assert.Equal(repo.Root, ProcessLauncher.RepositoryOf(repo.Root));
    }

    [Fact]
    public void AWorktreeCountsToo_WhereDotGitIsAFileRatherThanAFolder()
    {
        using var repo = new TempTree();
        repo.File(".git", "gitdir: /elsewhere/.git/worktrees/x");

        Assert.Equal(repo.Root, ProcessLauncher.RepositoryOf(repo.Root));
    }

    [Fact]
    public void SomewhereThatIsNotACheckoutHasNothingToPull()
    {
        using var plain = new TempTree();
        var deep = plain.Directory("scripts");

        Assert.Null(ProcessLauncher.RepositoryOf(deep));
    }

    [Fact]
    public void TheNearestRepositoryWins()
    {
        // A submodule, or a checkout inside a checkout: pulling the outer one would update
        // something other than the script about to run.
        using var outer = new TempTree();
        outer.Directory(".git");
        var inner = outer.Directory("vendor/tools");
        Directory.CreateDirectory(Path.Combine(inner, ".git"));

        Assert.Equal(inner, ProcessLauncher.RepositoryOf(inner));
    }

    [Fact]
    public void NothingToLookAtIsNotARepository()
    {
        Assert.Null(ProcessLauncher.RepositoryOf(null));
        Assert.Null(ProcessLauncher.RepositoryOf(""));
    }

    [Fact]
    public void ATargetOutsideACheckout_IsRunWithoutAPullAndWithoutANote()
    {
        // ProcessLauncher end to end, with nothing to pull — no git is started, so the
        // message is the plan's own. A note only ever reports a pull that went wrong.
        var plan = new ExecutionPlan
        {
            Kind = ExecutionKind.Script,
            Target = Path.Combine(Path.GetTempPath(), "klippy-no-such-script.sh"),
            PullFirst = true,
        };

        var result = ProcessLauncher.Run(plan);

        Assert.False(result.Started);
        Assert.Equal($"Script not found: {plan.Target}", result.Message);
    }

    // ---- the preference reaching the launcher ----

    private static SnippetStore NewStore(params Snippet[] snippets)
    {
        var store = new SnippetStore(
            Path.Combine(Path.GetTempPath(), $"klippy-pull-{Guid.NewGuid():N}.json"), seedIfEmpty: false);
        foreach (var snippet in snippets) store.Add(snippet);
        return store;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RunningAMarkedItem_CarriesThePreferenceToTheLauncher(bool pullFirst)
    {
        var settings = new AppSettings { ExecutePullFirst = pullFirst };
        var vm = new MainViewModel(
            NewStore(new Snippet { Label = "Deploy", Content = "/repo/tools/deploy.sh", IsExecutable = true }),
            settings: settings);

        ExecutionPlan? ran = null;
        vm.Executor = plan => { ran = plan; return Task.FromResult(new ExecutionResult(true, plan.Description)); };

        vm.ActivateCommand.Execute(vm.Filtered[0]);

        Assert.Equal(pullFirst, ran!.PullFirst);
    }

    [Fact]
    public void RunningAnUnmatchedSearch_CarriesItToo()
    {
        var settings = new AppSettings { ExecutePullFirst = true, ExecuteVerifyPaths = false };
        var vm = new MainViewModel(NewStore(), settings: settings);

        ExecutionPlan? ran = null;
        vm.Executor = plan => { ran = plan; return Task.FromResult(new ExecutionResult(true, plan.Description)); };

        vm.FilterText = "/repo/tools/deploy.sh";
        vm.RunOfferCommand.Execute(null);

        Assert.Equal(ExecutionKind.Script, ran!.Kind);
        Assert.True(ran.PullFirst);
    }

    [Fact]
    public void ThePreferenceIsReadPerRun_SoAToggleAppliesToTheVeryNextEnter()
    {
        // The same reasoning as the copy preferences: the Settings overlay writes through
        // to this instance, and a run made straight afterwards must see it.
        var settings = new AppSettings { ExecutePullFirst = false, ExecuteVerifyPaths = false };
        var vm = new MainViewModel(NewStore(), settings: settings);

        ExecutionPlan? ran = null;
        vm.Executor = plan => { ran = plan; return Task.FromResult(new ExecutionResult(true, plan.Description)); };

        vm.FilterText = "/repo/tools/deploy.sh";
        vm.RunOfferCommand.Execute(null);
        Assert.False(ran!.PullFirst);

        settings.ExecutePullFirst = true;
        vm.RunOfferCommand.Execute(null);
        Assert.True(ran!.PullFirst);
    }

    [Fact]
    public void ThePreferenceDefaultsOff()
    {
        // It is a network call made on the user's behalf, on every run.
        Assert.False(new AppSettings().ExecutePullFirst);
    }

    [Fact]
    public void ThePreferenceSurvivesASaveAndLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), $"klippy-settings-{Guid.NewGuid():N}.json");
        try
        {
            new AppSettings { ExecutePullFirst = true }.Save(path);
            Assert.True(AppSettings.Load(path).ExecutePullFirst);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A real directory tree, removed when the test ends.</summary>
    private sealed class TempTree : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), $"klippy-tree-{Guid.NewGuid():N}");

        public TempTree() => System.IO.Directory.CreateDirectory(Root);

        public string Directory(string relative)
        {
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public void File(string relative, string content) =>
            System.IO.File.WriteAllText(Path.Combine(Root, relative), content);

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Root, recursive: true); }
            catch (IOException) { /* a temp tree left behind is not a failed test */ }
        }
    }
}
