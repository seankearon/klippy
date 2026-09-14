using Klippy.Services;

namespace Klippy.ViewModels;

/// <summary>
/// The offer made when a search matched nothing but named something runnable: a link, a
/// folder, a script or application, or one of the machine's controls.
///
/// It is not a <see cref="RowViewModel"/> on purpose. Rows are items you copy or execute,
/// and this is neither — keeping it out of <c>Filtered</c> is what makes "an item match
/// always wins" structural rather than a rule to remember. It carries an
/// <see cref="ExecutionPlan"/>, so running it is the same gesture, through the same
/// engine, as running an item marked Execute.
/// </summary>
public sealed class OfferViewModel : ViewModelBase
{
    public ExecutionPlan Plan { get; }

    public OfferViewModel(ExecutionPlan plan) => Plan = plan;

    /// <summary>What pressing Enter will do, in a word or two.</summary>
    public string Verb => Plan.Kind switch
    {
        ExecutionKind.Url => "Open link",
        ExecutionKind.Folder => "Open folder",
        ExecutionKind.Script => "Run script",
        ExecutionKind.Application => "Start",
        ExecutionKind.System => Plan.Action.ToString(),
        _ => "",
    };

    /// <summary>What it will be done to: the link, the path, or the machine.</summary>
    public string Detail => Plan.Kind == ExecutionKind.System ? "this computer" : Plan.Target;

    /// <summary>
    /// The confirmation's headline, e.g. "Restart this computer?". Only a machine control
    /// is ever confirmed, so only that spelling names the machine.
    /// </summary>
    public string Question => Plan.Kind == ExecutionKind.System ? $"{Verb} this computer?" : $"{Verb}?";

    /// <summary>
    /// What it costs, said plainly. Only restart and hibernate/sleep take your work with
    /// them; locking is just a lock screen, and saying otherwise would train people to
    /// dismiss the dialog without reading it.
    /// </summary>
    public string Consequence => Plan.Action switch
    {
        SystemAction.Restart => "Anything unsaved will be lost.",
        SystemAction.Hibernate => "Everything is written to disk and the machine powers down.",
        SystemAction.Sleep => "The machine suspends; Klippy stays where it is.",
        _ => "You will need to sign in again.",
    };

    /// <summary>
    /// Whether this one asks before it happens. Only the machine controls ever do: opening
    /// a page or a folder is undone by closing it, while a restart is not undone at all.
    /// </summary>
    public bool NeedsConfirmation(AppSettings settings) =>
        Plan.Kind == ExecutionKind.System && settings.ExecuteConfirmSystemActions;
}
