using ManifestApp.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ManifestApp.Services;

/// <summary>
/// Serializes <see cref="ContentDialog"/> presentation on the UI thread.
///
/// WinUI 3 allows only a single ContentDialog open per thread; showing a second one
/// (e.g. from a re-entrant click or an overlapping async flow) throws a stowed
/// COMException (E_ABORT) that escapes <c>async void</c> handlers and hard-crashes the
/// process with 0xc000027b — bypassing <see cref="Application.UnhandledException"/>.
/// Routing every dialog through this gate makes those overlaps impossible and turns any
/// remaining failure into a graceful <see cref="ContentDialogResult.None"/> instead of a crash.
/// </summary>
internal static class DialogService
{
    private static bool _isShowing;

    /// <summary>
    /// Shows <paramref name="dialog"/> if no other dialog is currently open and the dialog
    /// has a live <see cref="ContentDialog.XamlRoot"/>. Never throws: overlaps and COM
    /// failures are logged and reported as <see cref="ContentDialogResult.None"/>.
    /// </summary>
    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        // A dialog is already visible — refuse the second one rather than crash the app.
        if (_isShowing)
        {
            AppLogger.Log("Suppressed a ContentDialog request while another dialog was already open.", "WARNING");
            return ContentDialogResult.None;
        }

        // ShowAsync on a detached/null XamlRoot also aborts with a stowed exception.
        if (dialog.XamlRoot is null)
        {
            AppLogger.Log("Suppressed a ContentDialog request because its XamlRoot was not set.", "WARNING");
            return ContentDialogResult.None;
        }

        _isShowing = true;
        try
        {
            try
            {
                return await dialog.ShowAsync();
            }
            catch (Exception)
            {
                // WinUI can transiently report that a dialog is still open while the previous one
                // finishes tearing down (a tight A-then-B dialog sequence). Give it a beat and retry once.
                await Task.Delay(150);
                return await dialog.ShowAsync();
            }
        }
        catch (Exception ex)
        {
            // Most likely the WinUI single-dialog / detached-root COMException. Degrade gracefully
            // rather than letting it escape an async void handler and fail-fast the process (0xc000027b).
            AppLogger.LogException("DialogService.ShowAsync", ex);
            return ContentDialogResult.None;
        }
        finally
        {
            _isShowing = false;
        }
    }
}
