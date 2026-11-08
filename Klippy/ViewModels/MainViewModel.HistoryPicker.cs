using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klippy.Services;

namespace Klippy.ViewModels;

/// <summary>
/// The history key pressed while an overlay has the window. Switching the list to the history would happen
/// behind the overlay, where nobody can see it, so the history comes up over the overlay instead — see
/// <see cref="HistoryPickerViewModel"/>.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The clipboard history shown over an overlay, or null. Drives the history picker.</summary>
    [ObservableProperty]
    private HistoryPickerViewModel? _historyPicker;

    /// <summary>
    /// What the history key does while an overlay is up: brings the picker over it, or puts the picker away
    /// if it is already showing. False when there is no overlay to come up over, or no history to show — the
    /// key then means what it always did, and the caller carries on with that.
    /// </summary>
    public bool ToggleHistoryPicker()
    {
        if (HistoryPicker is not null)
        {
            HistoryPicker = null;
            return true;
        }

        if (_history is null || !IsOverlayOpen) return false;

        HistoryPicker = new HistoryPickerViewModel(_history);
        return true;
    }

    [RelayCommand]
    private void CloseHistoryPicker() => HistoryPicker = null;

    /// <summary>
    /// Copies the clip and puts the picker away, leaving everything else as it was: the overlay underneath is
    /// what the clip was wanted for, so the window stays up whatever closing after a copy says, and the
    /// keyboard goes back to where it was for the paste.
    /// </summary>
    [RelayCommand]
    private async Task PickClip(ClipViewModel? row)
    {
        if (HistoryPicker is not { } picker || (row ?? picker.Selected) is not { } clip) return;

        HistoryPicker = null;
        await CopyCore(clip, record: false, quiet: true);
    }

    /// <summary>
    /// Whether <paramref name="stroke"/> is the history key. The window only ever sees it when the key is not
    /// registered system-wide — hotkeys switched off, or a plain window run — and answers it the same way.
    /// </summary>
    public bool IsHistoryHotkey(KeyStroke stroke) =>
        _prefs.ParsedHistoryHotkey is { } spec && SnippetHotkeys.ToStroke(spec) == stroke;
}
