using Klippy.Services;

namespace Klippy.ViewModels;

/// <summary>
/// The offer made when a search matched nothing but named something runnable: a link, a
/// folder, a script or application, or one of the machine's controls — or, in the one case
/// that runs nothing at all, Klippy's own <c>quit</c>.
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

    /// <summary>
    /// Klippy's own quit, which stands where a runnable offer stands and deliberately is
    /// not one: it carries no plan, never reaches <see cref="ProcessLauncher"/>, and is not
    /// behind the setting that governs running typed text — being unwilling to run a typed
    /// path is no reason to be unable to close the app. It shares this class because from
    /// the keyboard it is the same gesture: type a word, press <c>Enter</c>, answer the
    /// dialog.
    /// </summary>
    public bool IsQuit { get; private init; }

    public OfferViewModel(ExecutionPlan plan) => Plan = plan;

    /// <inheritdoc cref="IsQuit"/>
    public static OfferViewModel Quit() => new(new ExecutionPlan()) { IsQuit = true };

    /// <summary>What pressing Enter will do, in a word or two.</summary>
    public string Verb => IsQuit ? "Quit Klippy" : Plan.Kind switch
    {
        ExecutionKind.Url => "Open link",
        ExecutionKind.Folder => "Open folder",
        ExecutionKind.Script => "Run script",
        ExecutionKind.Application => "Start",
        ExecutionKind.System => Plan.Action.ToString(),
        _ => "",
    };

    /// <summary>
    /// The badge that says what Enter does here. "run" for everything that starts
    /// something, which quit is precisely not — a band reading "Quit Klippy · ↵ run" would
    /// say the opposite of what the key is about to do.
    /// </summary>
    public string KeyBadge => IsQuit ? "↵ quit" : "↵ run";

    /// <summary>What it will be done to: the link, the path, the machine, or Klippy itself.</summary>
    public string Detail => IsQuit
        ? "stop listening and leave the tray"
        : Plan.Kind == ExecutionKind.System ? "this computer" : Plan.Target;

    /// <summary>
    /// The confirmation's headline, e.g. "Restart this computer?". Only a machine control
    /// is ever confirmed, so only that spelling names the machine — and quit names neither,
    /// since "Quit Klippy" already says what it is.
    /// </summary>
    public string Question => IsQuit
        ? $"{Verb}?"
        : Plan.Kind == ExecutionKind.System ? $"{Verb} this computer?" : $"{Verb}?";

    /// <summary>
    /// What it costs, said plainly. Only restart and hibernate/sleep take your work with
    /// them; locking is just a lock screen, and saying otherwise would train people to
    /// dismiss the dialog without reading it. Quitting costs no data at all — what goes
    /// with it is the hotkeys and the tray icon, which is the thing worth saying.
    /// </summary>
    public string Consequence => IsQuit
        ? "The hotkeys stop answering until Klippy is started again. Snippets and clipboard history are already saved."
        : Plan.Action switch
        {
            SystemAction.Restart => "Anything unsaved will be lost.",
            SystemAction.Hibernate => "Everything is written to disk and the machine powers down.",
            SystemAction.Sleep => "The machine suspends; Klippy stays where it is.",
            _ => "You will need to sign in again.",
        };

    /// <summary>
    /// Whether this one asks before it happens. Only the machine controls ever do: opening
    /// a page or a folder is undone by closing it, while a restart is not undone at all.
    ///
    /// Quit always asks, and unlike the machine controls it is not the setting's to waive:
    /// that setting is part of the execution feature, and this is the one offer that
    /// executes nothing.
    /// </summary>
    public bool NeedsConfirmation(AppSettings settings) =>
        IsQuit || (Plan.Kind == ExecutionKind.System && settings.ExecuteConfirmSystemActions);
}
