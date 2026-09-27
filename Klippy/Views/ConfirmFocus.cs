using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Klippy.Views;

/// <summary>
/// Where the keyboard is while a confirmation is up. Each of Klippy's asks about something
/// that cannot be taken back, so it opens on Cancel: a stray Enter — or the Enter that asked,
/// held a beat too long — goes back rather than ahead. Going ahead is Tab or an arrow away,
/// then Enter or Space, as with any button.
///
/// Tab goes round the two buttons rather than out to the list behind the scrim, and once the
/// dialog closes the keyboard goes back to whatever had it before, which after a typed
/// <c>restart</c> is the search box.
/// </summary>
internal static class ConfirmFocus
{
    /// <param name="overlay">What shows and hides the confirmation, scrim and all.</param>
    /// <param name="cancel">Where the keyboard lands when the dialog opens.</param>
    /// <param name="confirm">The button that goes ahead.</param>
    public static void Attach(Control overlay, Button cancel, Button confirm)
    {
        IInputElement? returnTo = null;

        KeyboardNavigation.SetTabNavigation(overlay, KeyboardNavigationMode.Cycle);

        overlay.PropertyChanged += (_, e) =>
        {
            if (e.Property != Visual.IsVisibleProperty) return;

            if (overlay.IsVisible)
            {
                returnTo = TopLevel.GetTopLevel(overlay)?.FocusManager?.GetFocusedElement();
                // As Tab rather than as a click would, so the button shows it has the
                // keyboard: nothing else on screen says which way Enter will go.
                cancel.Focus(NavigationMethod.Tab);
            }
            else
            {
                // Only while it is still in the window: the rows behind the scrim can be
                // rebuilt while the dialog is up, taking a focused button with them.
                if (returnTo is Visual previous && TopLevel.GetTopLevel(previous) is not null)
                    returnTo.Focus();
                returnTo = null;
            }
        };

        // Two buttons side by side, so every arrow names one of them.
        overlay.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            var target = e.Key switch
            {
                Key.Left or Key.Up => cancel,
                Key.Right or Key.Down => confirm,
                _ => null,
            };
            if (target is null) return;

            target.Focus(NavigationMethod.Directional);
            e.Handled = true;
        });
    }
}
