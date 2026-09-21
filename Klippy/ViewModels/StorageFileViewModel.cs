using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klippy.Services;

namespace Klippy.ViewModels;

/// <summary>
/// One of Klippy's files on the Settings screen: a box holding the path, the line under
/// it saying what that path resolves to and what is there, and the buttons that open the
/// file or the folder it is in.
///
/// One row per file rather than one folder for all of them, because the files want
/// different answers. A snippet store is worth syncing between a Mac and a Windows box;
/// the variables file — the one that says where <c>%ws%</c> is on *this* machine — is
/// exactly what must not follow it there, and a record of everything copied on one
/// machine is the last thing to want turning up on the other.
///
/// A bare name lands in the data folder and an absolute path is taken as given, so the
/// same box does both without a mode to pick.
/// </summary>
public partial class StorageFileViewModel : ViewModelBase
{
    private readonly string _defaultName;
    private readonly Action<string> _write;
    private readonly Func<string, string>? _describe;
    private readonly Func<string?>? _inUse;
    private readonly string? _template;
    private readonly Func<ExecutionPlan, Task<ExecutionResult>>? _executor;
    private readonly Action<string?> _report;

    /// <summary>Suppresses writing back while the constructor seeds the box.</summary>
    private readonly bool _loaded;

    /// <summary>
    /// The path as configured. Editable, because the whole reason to look at it is to
    /// point it somewhere else — and hunting down settings.json to do that is the sort of
    /// errand this screen exists to spare you.
    /// </summary>
    [ObservableProperty]
    private string _file;

    /// <summary>What this file is, in the heading above the box: "SNIPPETS".</summary>
    public string Label { get; }

    /// <summary>The one-line explanation beside it.</summary>
    public string Hint { get; }

    /// <summary>The name an empty box means, shown in the box while it is empty.</summary>
    public string Watermark => _defaultName;

    /// <summary>
    /// What <see cref="File"/> actually resolves to, which is the thing the buttons act on
    /// and the only form worth showing for a bare name.
    /// </summary>
    public string Path { get; private set; } = "";

    /// <summary>What is at that path, in a few words: the state the row is reporting.</summary>
    public string Summary { get; private set; } = "";

    /// <summary>Whether the file is there, which decides what the open button offers to do.</summary>
    public bool FileExists { get; private set; }

    /// <summary>
    /// Whether the running app has a different file of this kind open. Every file here but
    /// the variables one is read at startup, because a store that changed file mid-session
    /// would have to decide what to do with the one it already had open — so a path typed
    /// here is a promise about the next launch, and saying so beats appearing to do nothing.
    /// </summary>
    public bool NeedsRestart { get; private set; }

    /// <summary>
    /// The line under the box: the count or the state, with the resolved path in front of
    /// it only when that is not simply what was typed. A bare name is worth resolving on
    /// screen; an absolute one is already the answer, and repeating it reads like a second
    /// setting — including when it was typed with the other separator, which is a
    /// respelling of the answer rather than a new one.
    /// </summary>
    public string StatusText =>
        string.Equals(
            StorageLocations.NormalizeSeparators(File?.Trim() ?? ""),
            Path,
            StringComparison.Ordinal)
            ? Summary
            : $"{Path}  —  {Summary}";

    /// <summary>"Open" once the file is there, "Create" while it is not.</summary>
    public string OpenVerb => FileExists ? "Open" : "Create";

    /// <summary>
    /// Whether there is an open button at all. Mobile has no launcher to open anything
    /// with, and a file Klippy does not know how to write cannot be offered a "Create" —
    /// the store that owns it writes it the first time it has something to say.
    /// </summary>
    public bool CanOpen => _executor is not null && (FileExists || _template is not null);

    /// <summary>Whether the folder button can do anything. The launcher again.</summary>
    public bool CanOpenFolder => _executor is not null;

    /// <param name="defaultName">What an empty box means — the usual name in the data folder.</param>
    /// <param name="read">The setting as stored, which is what the box starts out showing.</param>
    /// <param name="write">Writes it back and saves. Called with the trimmed text, empty for the default.</param>
    /// <param name="describe">
    /// What to say about the file instead of the usual "in use" / "no file yet" — the
    /// variables row counts what it parsed, because a count is how you know a hand-edit took.
    /// </param>
    /// <param name="inUse">
    /// The file the running app has open, or null where nothing holds one (the variables
    /// file, which is re-read as it changes, and a history that is switched off).
    /// </param>
    /// <param name="template">What to write when the file is not there yet, or null for no Create button.</param>
    /// <param name="report">Where a failure to open or write goes — the one status line the screen has.</param>
    public StorageFileViewModel(
        string label,
        string defaultName,
        string hint,
        Func<string> read,
        Action<string> write,
        Action<string?> report,
        Func<ExecutionPlan, Task<ExecutionResult>>? executor = null,
        Func<string, string>? describe = null,
        Func<string?>? inUse = null,
        string? template = null)
    {
        Label = label;
        Hint = hint;
        _defaultName = defaultName;
        _write = write;
        _report = report;
        _executor = executor;
        _describe = describe;
        _inUse = inUse;
        _template = template;

        _file = read();
        Reread();
        _loaded = true;
    }

    /// <summary>
    /// Works out where the path lands and what is there. Called as the screen opens, after
    /// an edit, and after the file is created — the line beside a path is only worth
    /// showing while it is current.
    /// </summary>
    public void Reread()
    {
        Path = StorageLocations.ResolveFile(File, _defaultName);
        FileExists = Exists(Path);
        NeedsRestart = _inUse?.Invoke() is { } open && !SamePath(open, Path);

        Summary =
            _describe is { } describe ? describe(Path)
            : NeedsRestart ? "takes effect on restart"
            : FileExists ? "in use"
            : "no file yet";

        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(FileExists));
        OnPropertyChanged(nameof(NeedsRestart));
        OnPropertyChanged(nameof(OpenVerb));
        OnPropertyChanged(nameof(CanOpen));
    }

    /// <summary>
    /// An empty box means the default name rather than no file, since there is no such
    /// thing as "no snippets file": one that is not there is one Klippy writes on the next
    /// save. Saved as typed otherwise, so the box keeps showing what was written rather
    /// than rewriting it under the cursor.
    /// </summary>
    partial void OnFileChanged(string value)
    {
        if (!_loaded) return;

        _write(value?.Trim() ?? "");
        Reread(); // the path moved, so the line beside it is about a different file
    }

    /// <summary>
    /// Opens the file in whatever the machine opens that kind with, writing the template
    /// first when there is nothing there yet.
    ///
    /// Creating it here rather than at startup: an empty file in everyone's app-data
    /// folder would be a file to wonder about, whereas one written the moment you ask to
    /// see it is the answer to the question you just asked.
    /// </summary>
    [RelayCommand]
    private async Task Open()
    {
        if (!FileExists && !TryWriteTemplate()) return;

        await Launch(new ExecutionPlan { Kind = ExecutionKind.Document, Target = Path });
    }

    /// <summary>Opens the folder the file is in, existing or not.</summary>
    [RelayCommand]
    private Task OpenFolder()
    {
        var folder = System.IO.Path.GetDirectoryName(Path);
        return string.IsNullOrEmpty(folder)
            ? Task.CompletedTask
            : Launch(new ExecutionPlan { Kind = ExecutionKind.Folder, Target = folder });
    }

    private async Task Launch(ExecutionPlan plan)
    {
        if (_executor is not { } run) return;

        var result = await run(plan);
        _report(result.Started ? null : result.Message);
    }

    /// <summary>
    /// Writes the template. Returns whether it worked — a folder that cannot be written is
    /// reported in the same line a failed save is, rather than opening an editor on a file
    /// that is not there.
    /// </summary>
    private bool TryWriteTemplate()
    {
        if (_template is not { } template) return false;

        try
        {
            var folder = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            System.IO.File.WriteAllText(Path, template);
            Reread();
            _report(null);
            return true;
        }
        catch (Exception ex)
        {
            _report($"Could not create {Path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Never throws: a path the OS will not even stat is one that is not there.</summary>
    private static bool Exists(string path)
    {
        try
        {
            return System.IO.File.Exists(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether two resolved paths name the same file. Case-insensitively, as
    /// <see cref="SnippetStore"/> compares them: the two that matter here came from the
    /// same machine, and a Windows user who retypes their own path in another case has
    /// not asked for anything to change.
    /// </summary>
    private static bool SamePath(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
