using Avalonia.Input;

namespace Klippy.Services;

/// <summary>
/// The keys Klippy's own window already answers to, so a snippet's shortcut can be kept off them.
///
/// The window's handler (<c>MainWindow.PreviewKeyDown</c>) sees every press on its way down, before the search
/// box does, and acts on a fixed set: Ctrl/⌘ with N, F, E, D, I, P, R, comma or Enter — whatever else is held
/// with it — and F2, the arrows, Enter and Esc. A snippet shortcut that starts on one of those could never be
/// reached. Nor could one that starts on a key the search box uses for editing, since that box has the
/// keyboard almost all the time: select all, copy, cut, paste, undo and redo, and moving through the line by
/// word or to either end.
///
/// Only the <em>first</em> press of a shortcut is checked. The second press of a chord is heard ahead of all
/// of these, so <c>Ctrl+K, Ctrl+N</c> is free to mean something of its own.
/// </summary>
public static class KlippyKeys
{
    /// <summary>What already answers to <paramref name="stroke"/> on this platform, or null when nothing does.</summary>
    public static string? OwnerOf(KeyStroke stroke) => OwnerOf(stroke, System.OperatingSystem.IsMacOS());

    /// <summary>
    /// What already answers to <paramref name="stroke"/> — "New snippet", "the search box" — or null when
    /// nothing does. <paramref name="mac"/> picks the command key: ⌘ there, Ctrl everywhere else.
    /// </summary>
    public static string? OwnerOf(KeyStroke stroke, bool mac)
    {
        var command = mac ? KeyModifiers.Meta : KeyModifiers.Control;
        var mods = stroke.Modifiers;

        // The window's command keys take the press whatever else is held with them.
        if ((mods & command) != 0)
        {
            var name = stroke.Key switch
            {
                Key.N => "New snippet",
                Key.F => "Focus search",
                Key.E => "Export / import",
                Key.D => "Duplicate",
                Key.I => "Edit",
                Key.P => "Toggle the preview",
                Key.R => mac ? "Show in Finder" : "Show in Explorer",
                Key.OemComma => "Settings",
                Key.Enter => "Copy",
                _ => null,
            };
            if (name is not null) return name;

            // The search box's own editing keys, held exactly as it expects them.
            if (mods == command && stroke.Key is Key.A or Key.C or Key.V or Key.X or Key.Z or Key.Y)
                return "the search box";
            if (mods == (command | KeyModifiers.Shift) && stroke.Key == Key.Z)
                return "the search box";

            if (mac && mods == KeyModifiers.Meta && stroke.Key is Key.Q or Key.H or Key.M)
                return "macOS";
        }

        // F2, the arrows, Enter and Esc are the window's however they are held.
        if (stroke.Key is Key.F2) return "Edit";
        if (stroke.Key is Key.Up or Key.Down) return "the list";
        if (stroke.Key is Key.Enter) return "Copy";
        if (stroke.Key is Key.Escape) return "Esc";

        // Moving through the line by word, or to either end, held with any modifier.
        if (mods != KeyModifiers.None && stroke.Key is Key.Left or Key.Right or Key.Home or Key.End
                or Key.Back or Key.Delete)
            return "the search box";

        return null;
    }
}
