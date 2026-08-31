using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Klippy.Services;

/// <summary>App preferences, kept separate from snippet data so an import can never clobber them.</summary>
public sealed class AppSettings
{
    /// <summary>Global hotkey that summons the window, e.g. "Ctrl+Alt+K".</summary>
    public string Hotkey { get; set; } = HotkeySpec.PlatformDefault;

    /// <summary>Set false to leave the key alone entirely.</summary>
    public bool HotkeyEnabled { get; set; } = true;

    /// <summary>Whether to record what gets copied. Desktop only; ignored where the OS forbids capture.</summary>
    public bool HistoryEnabled { get; set; } = true;

    /// <summary>How many clips to keep before the oldest unpinned ones are dropped.</summary>
    public int HistoryLimit { get; set; } = ClipHistoryStore.DefaultCapacity;

    /// <summary>
    /// Keep history in memory only, so nothing copied is ever written to disk. The
    /// history is a record of everything that passed through the clipboard, and that is
    /// a thing a person is entitled to decline.
    /// </summary>
    public bool HistorySessionOnly { get; set; }

    /// <summary>
    /// Applications never recorded from, by process name ("keepass"). Password managers
    /// already mark their own clipboard writes and those are honoured regardless — this
    /// is for anything else the user would rather not have kept.
    /// </summary>
    public string[] HistoryExcludedApps { get; set; } = Array.Empty<string>();

    [JsonIgnore]
    public HotkeySpec ParsedHotkey =>
        HotkeySpec.TryParse(Hotkey, out var spec) ? spec : HotkeySpec.Default;

    public static string FilePath => StorageLocations.SettingsPath;

    /// <summary>Never throws: unreadable settings fall back to defaults.</summary>
    public static AppSettings Load(string? path = null)
    {
        path ??= FilePath;
        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                if (JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings) is { } loaded)
                    return loaded;
            }
        }
        catch (Exception)
        {
            // fall through to defaults
        }
        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        path ??= FilePath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        using (var stream = File.Create(tmp))
            JsonSerializer.Serialize(stream, this, SettingsJsonContext.Default.AppSettings);
        File.Move(tmp, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
public sealed partial class SettingsJsonContext : JsonSerializerContext;
