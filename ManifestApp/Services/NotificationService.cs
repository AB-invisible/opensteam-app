using Windows.UI.Notifications;
using Windows.Data.Xml.Dom;

namespace ManifestApp.Services;

/// <summary>
/// Service wrapper using native WinRT APIs to trigger Windows Toast Notifications.
/// Works cleanly in unpackaged WinUI 3 desktop applications.
/// </summary>
internal static class NotificationService
{
    private const string AppId = "OpenSteamApp";

    /// <summary>
    /// Displays a standard Windows toast notification banner with a title and message.
    /// </summary>
    /// <param name="title">Title text of the notification banner.</param>
    /// <param name="message">Body/description text of the notification banner.</param>
    public static void ShowToast(string title, string message)
    {
        try
        {
            // Fetch the standard XML template for a two-line toast notification (title + body)
            XmlDocument toastXml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);

            // Populate the text fields in the XML template
            XmlNodeList textNodes = toastXml.GetElementsByTagName("text");
            if (textNodes.Count >= 2)
            {
                textNodes[0].AppendChild(toastXml.CreateTextNode(title));
                textNodes[1].AppendChild(toastXml.CreateTextNode(message));
            }
            else if (textNodes.Count == 1)
            {
                textNodes[0].AppendChild(toastXml.CreateTextNode($"{title}: {message}"));
            }

            // Construct the native ToastNotification instance
            ToastNotification toast = new ToastNotification(toastXml);

            // Trigger the notification using our custom Application User Model ID (AUMID)
            ToastNotificationManager.CreateToastNotifier(AppId).Show(toast);
        }
        catch (Exception ex)
        {
            // Toast notifications must never crash the host application.
            System.Diagnostics.Debug.WriteLine($"[NotificationService] Failed to display toast: {ex.Message}");
        }
    }
}
