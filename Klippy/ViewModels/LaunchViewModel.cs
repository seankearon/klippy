using CommunityToolkit.Mvvm.ComponentModel;
using Klippy.Services;

namespace Klippy.ViewModels;

/// <summary>
/// The offer made when a search matched nothing but named something runnable: a URL, a
/// folder, a program, or one of the OS controls.
///
/// It is not a <see cref="RowViewModel"/> on purpose. Rows are things you copy, and this is
/// the one thing in the list area that is not — keeping it out of <c>Filtered</c> is what
/// makes "an item match always wins" structural rather than a rule to remember.
/// </summary>
public partial class LaunchViewModel : ViewModelBase
{
    public LaunchTarget Target { get; }

    /// <summary>Set when the launch itself failed, and shown where the offer was.</summary>
    [ObservableProperty]
    private string? _error;

    public LaunchViewModel(LaunchTarget target) => Target = target;

    /// <summary>What pressing Enter will do, in one or two words.</summary>
    public string Verb => Target.Kind switch
    {
        LaunchKind.Url => "Open link",
        LaunchKind.Folder => "Open folder",
        LaunchKind.File => LaunchPolicy.IsExecutable(Target.Target) ? "Run" : "Open",
        LaunchKind.System => Target.Action.ToString(),
        _ => "",
    };

    /// <summary>What it will be done to: the URL, the path, or the machine.</summary>
    public string Detail => Target.Kind == LaunchKind.System ? "this computer" : Target.Target;

    /// <summary>
    /// The confirmation's headline, e.g. "Restart this computer?". Only an OS control is
    /// ever confirmed, so only that spelling names the machine.
    /// </summary>
    public string Question => Target.Kind == LaunchKind.System ? $"{Verb} this computer?" : $"{Verb}?";

    /// <summary>
    /// What it costs, said plainly. Only restart and hibernate/sleep take your work with
    /// them; locking is just a lock screen, and saying otherwise would train people to
    /// dismiss the dialog without reading it.
    /// </summary>
    public string Consequence => Target.Action switch
    {
        SystemAction.Restart => "Anything unsaved will be lost.",
        SystemAction.Hibernate => "Everything is written to disk and the machine powers down.",
        SystemAction.Sleep => "The machine suspends; Klippy stays where it is.",
        _ => "You will need to sign in again.",
    };

    /// <summary>
    /// Whether this one asks before it happens. Only the OS controls ever do: opening a
    /// folder or a page is undone by closing it, while a restart is not undone at all.
    /// </summary>
    public bool NeedsConfirmation(AppSettings settings) =>
        Target.Kind == LaunchKind.System && settings.LaunchConfirmSystemActions;
}
