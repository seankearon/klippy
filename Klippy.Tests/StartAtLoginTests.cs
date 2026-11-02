using System;
using System.IO;
using Avalonia.Headless.XUnit;
using Klippy.Services;
using Klippy.ViewModels;
using Klippy.Views;
using Xunit;

namespace Klippy.Tests;

/// <summary>
/// Starting Klippy when you log in. The text each platform keeps — Windows' Run value, macOS' LaunchAgent — is
/// pinned down here from any machine; the registry write itself is the desktop head's and is not reachable from a
/// test. The Settings toggle is driven against a described registration, as the run rules are against a described
/// environment.
/// </summary>
public class StartAtLoginTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"klippy-login-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ---- the argument a login start carries ----

    [Fact]
    public void ALoginStart_SaysSo_AndNothingElseDoes()
    {
        Assert.True(LoginItem.StartsInBackground(["--background"]));
        Assert.True(LoginItem.StartsInBackground(["--other", "--BACKGROUND"]));
        Assert.False(LoginItem.StartsInBackground([]));
        Assert.False(LoginItem.StartsInBackground(null));
        Assert.False(LoginItem.StartsInBackground(["background"]));
    }

    // ---- Windows: the Run value ----

    [Fact]
    public void TheRunValue_QuotesThePath_SoProgramFilesStaysOnePath()
    {
        Assert.Equal(@"""C:\Program Files\Klippy\Klippy.Desktop.exe"" --background",
            LoginItem.WindowsCommand(@"C:\Program Files\Klippy\Klippy.Desktop.exe"));
    }

    [Theory]
    [InlineData(@"""C:\Program Files\Klippy\Klippy.Desktop.exe"" --background", @"C:\Program Files\Klippy\Klippy.Desktop.exe")]
    [InlineData(@"C:\tools\Klippy.Desktop.exe --background", @"C:\tools\Klippy.Desktop.exe")]   // written by hand
    [InlineData(@"C:\tools\Klippy.Desktop.exe", @"C:\tools\Klippy.Desktop.exe")]
    [InlineData(@"""C:\unterminated\Klippy.Desktop.exe", @"C:\unterminated\Klippy.Desktop.exe")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void TheRunValue_IsReadBackToTheProgramItStarts(string? command, string? program) =>
        Assert.Equal(program, LoginItem.WindowsProgram(command));

    [Theory]
    [InlineData(null, false)]                                             // never touched: enabled
    [InlineData(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)] // enabled
    [InlineData(new byte[] { 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)] // enabled, as some builds write it
    [InlineData(new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]  // disabled in Task Manager
    [InlineData(new byte[] { 7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    public void TaskManagersSwitch_IsRead_AsWhatHappensAtTheNextLogin(byte[]? record, bool disabled) =>
        Assert.Equal(disabled, LoginItem.IsDisabledByWindows(record));

    // ---- macOS: the LaunchAgent ----

    [Fact]
    public void AnAppBundle_IsStartedThroughOpen_InTheBackground()
    {
        Assert.Equal(
            new[] { "/usr/bin/open", "-g", "-a", "/Applications/Klippy.app", "--args", "--background" },
            LoginItem.MacProgramArguments("/Applications/Klippy.app/Contents/MacOS/Klippy.Desktop"));
    }

    [Fact]
    public void ABareExecutable_IsStartedItself()
    {
        Assert.Equal(new[] { "/Users/sam/src/klippy/bin/Klippy.Desktop", "--background" },
            LoginItem.MacProgramArguments("/Users/sam/src/klippy/bin/Klippy.Desktop"));
    }

    [Fact]
    public void ThePlist_ReadsBackToWhatItRuns_AndRunsAtLoadOnly()
    {
        // An ampersand in a folder name has to survive the trip through XML.
        var arguments = LoginItem.MacProgramArguments("/Applications/R&D/Klippy.app/Contents/MacOS/Klippy.Desktop");
        var plist = LoginItem.MacPlist(arguments);

        Assert.Contains("<key>RunAtLoad</key>", plist);
        Assert.DoesNotContain("KeepAlive", plist); // quitting Klippy leaves it quit until the next login
        Assert.Contains($"<string>{LoginItem.MacLabel}</string>", plist);
        Assert.Contains("R&amp;D", plist);

        var readBack = LoginItem.MacProgramArguments(new StringReader(plist));
        Assert.Equal(arguments, readBack);
        Assert.Equal("/Applications/R&D/Klippy.app", LoginItem.MacProgram(readBack));
    }

    [Theory]
    [InlineData("not xml at all")]
    [InlineData("<plist><dict><key>Label</key><string>x</string></dict></plist>")]
    public void APlistWithNothingToRun_ReadsAsNothing(string text) =>
        Assert.Null(LoginItem.MacProgram(LoginItem.MacProgramArguments(new StringReader(text))));

    [Fact]
    public void TheAgent_IsWrittenRead_AndTakenAway()
    {
        var plist = LoginItem.MacPlistPath(_root);
        var item = new LaunchAgentLoginItem(plist, "/Applications/Klippy.app/Contents/MacOS/Klippy.Desktop");
        Assert.Null(item.Read());

        item.Register();
        Assert.Equal(Path.Combine(_root, "Library", "LaunchAgents", "com.seankearon.klippy.login.plist"), plist);
        Assert.Equal(new LoginRegistration("/Applications/Klippy.app", IsThisCopy: true), item.Read());

        item.Unregister();
        Assert.False(File.Exists(plist));
        Assert.Null(item.Read());
        item.Unregister(); // nothing there is not a failure
    }

    [Fact]
    public void AnAgentLeftByAnotherCopy_IsOn_ButNotThisCopy()
    {
        var plist = LoginItem.MacPlistPath(_root);
        new LaunchAgentLoginItem(plist, "/Applications/Klippy.app/Contents/MacOS/Klippy.Desktop").Register();

        var dev = new LaunchAgentLoginItem(plist, "/Users/sam/src/klippy/bin/Klippy.Desktop");

        Assert.Equal(new LoginRegistration("/Applications/Klippy.app", IsThisCopy: false), dev.Read());
    }

    // ---- the Settings toggle ----

    /// <summary>A registration described rather than kept, with a switch to make writing it fail.</summary>
    private sealed class FakeLoginItem(LoginRegistration? registered = null) : ILoginItem
    {
        public LoginRegistration? Registered { get; set; } = registered;
        public string? Refusal { get; set; }
        public int Writes { get; private set; }

        public LoginRegistration? Read() => Registered;

        public void Register()
        {
            Writes++;
            if (Refusal is { } why) throw new UnauthorizedAccessException(why);
            Registered = new LoginRegistration(@"C:\Klippy\Klippy.Desktop.exe", IsThisCopy: true);
        }

        public void Unregister()
        {
            Writes++;
            if (Refusal is { } why) throw new UnauthorizedAccessException(why);
            Registered = null;
        }
    }

    private static SettingsViewModel Settings(ILoginItem? item) =>
        new(new AppSettings { SourcePath = Path.Combine(Path.GetTempPath(), $"klippy-login-{Guid.NewGuid():N}.json") },
            close: () => { }, loginItem: item);

    [Fact]
    public void WhereNothingKeepsIt_TheToggleIsNotOffered() =>
        Assert.False(Settings(null).ShowStartAtLogin);

    [Fact]
    public void TheToggle_SaysWhatThePlatformSays_AndWritesNothingToSay()
    {
        var on = new FakeLoginItem(new LoginRegistration(@"C:\Klippy\Klippy.Desktop.exe", IsThisCopy: true));
        var vm = Settings(on);

        Assert.True(vm.ShowStartAtLogin);
        Assert.True(vm.StartAtLogin);
        Assert.Equal(0, on.Writes); // reading it is not registering it again

        Assert.False(Settings(new FakeLoginItem()).StartAtLogin);
    }

    [Fact]
    public void Flipping_RegistersAndUnregisters()
    {
        var item = new FakeLoginItem();
        var vm = Settings(item);

        vm.StartAtLogin = true;
        Assert.NotNull(item.Registered);

        vm.StartAtLogin = false;
        Assert.Null(item.Registered);
        Assert.Equal(2, item.Writes);
    }

    [Fact]
    public void AWriteThatIsRefused_LeavesTheToggleWhereItWas_AndSaysWhy()
    {
        var item = new FakeLoginItem { Refusal = "Access to the registry key is denied." };
        var vm = Settings(item);

        vm.StartAtLogin = true;

        Assert.False(vm.StartAtLogin);
        Assert.Contains("Access to the registry key is denied.", vm.StatusText);
    }

    [Fact]
    public void AnotherCopysRegistration_IsNamed_RatherThanPassedOffAsThisOne()
    {
        var vm = Settings(new FakeLoginItem(new LoginRegistration(@"D:\portable\Klippy.Desktop.exe", IsThisCopy: false)));

        Assert.True(vm.StartAtLogin);
        Assert.StartsWith(@"Starts D:\portable\Klippy.Desktop.exe — not this copy", vm.StartAtLoginHint);

        // Off and on again takes it over for this copy.
        vm.StartAtLogin = false;
        vm.StartAtLogin = true;
        Assert.DoesNotContain("not this copy", vm.StartAtLoginHint);
    }

    // ---- a start that stays put away ----

    [AvaloniaFact]
    public void AWindowNeverShown_CanStillCopyAndRun_OnceConnected()
    {
        // A login start never shows the window, and a snippet's hotkey copies or runs without showing it — so
        // what the window hands the view model as it opens has to be there without the opening.
        var store = new SnippetStore(Path.Combine(_root, "snippets.json"), seedIfEmpty: false);
        var vm = new MainViewModel(store);
        var window = new MainWindow { DataContext = vm };
        Assert.Null(vm.Executor);

        window.ConnectToPlatform();

        Assert.False(window.IsVisible);
        Assert.NotNull(vm.Executor);
        Assert.NotNull(vm.ClipboardWriter);
        Assert.NotNull(vm.ClipboardReader);
    }
}
