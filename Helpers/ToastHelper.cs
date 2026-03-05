using System;

namespace AxfsExplorer.Helpers;

static class ToastHelper
{
    static bool _registered;

    public static void Initialize()
    {
        if (_registered)
            return;
        try
        {
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch
        { /* Unpackaged app or missing identity — toasts unavailable */
        }
    }

    public static void Show(string title, string message)
    {
        if (!_registered)
            return;
        try
        {
            var notif = new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                .AddText(title)
                .AddText(message)
                .BuildNotification();
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Show(notif);
        }
        catch { }
    }
}
