namespace XE_Local_AI_Engine.Desktop;

using System.Globalization;

internal static class DesktopText
{
    // The SPA's i18nextLng choice wins once known; the OS culture is only the first-run fallback.
    internal static string Language { get; private set; } = Normalize(CultureInfo.CurrentUICulture.Name) ?? "en";
    private static bool German => Language == "de";

    /// <summary>Maps a raw language value (possibly JSON-quoted, possibly a region tag) to "de" or "en"; null when
    ///     nothing is known, so the caller keeps its current language.</summary>
    internal static string? Normalize(string? value)
    {
        var text = value?.Trim().Trim('"').Trim();
        if (string.IsNullOrEmpty(text) || text == "null")
        {
            return null;
        }

        var separator = text.IndexOfAny(['-', '_']);
        var primary = separator < 0 ? text : text[..separator];
        return primary.Equals("de", StringComparison.OrdinalIgnoreCase) ? "de" : "en";
    }

    internal static bool Apply(string? language)
    {
        if (Normalize(language) is not { } normalized || normalized == Language)
        {
            return false;
        }

        Language = normalized;
        return true;
    }

    internal static string Starting => German ? "XE wird gestartet…" : "Starting XE…";
    internal static string Closing => German ? "XE wird beendet…" : "Stopping XE…";
    internal static string Open => German ? "XE öffnen" : "Open XE";
    internal static string Settings => German ? "Desktop-Einstellungen" : "Desktop settings";
    internal static string Quit => German ? "XE beenden" : "Quit XE";
    internal static string QuitQuestion => German ? "XE beenden und laufende Arbeit unterbrechen?" : "Quit XE and interrupt running work?";
    internal static string Cancel => German ? "Abbrechen" : "Cancel";
    internal static string CloseQuestion => German ? "Was soll beim Schließen des Fensters geschehen?" : "What should closing the window do?";
    internal static string KeepInTray => German ? "Im Infobereich weiterlaufen" : "Keep running in the tray";
    internal static string Remember => German ? "Auswahl merken" : "Remember my choice";
    internal static string Ask => German ? "Bei jedem Schließen fragen" : "Ask every time";
    internal static string NoTray => German ? "Kein sicher verfügbarer Infobereich. Das Fenster bleibt sichtbar." : "A usable tray is unavailable. XE will not hide the window.";
    internal static string Attached => German ? "Ein separat gestarteter Engine-Prozess läuft beim Beenden weiter." : "A separately started engine will keep running when XE closes.";
    internal static string Owned => German ? "Beim Beenden werden die Engine und ihre lokalen Laufzeiten gestoppt." : "Quitting stops the engine and its local runtimes.";
    internal static string StartupFailed => German
        ? "XE konnte nicht gestartet werden. Prüfen Sie die Installation, den Datenordner und die Engine-Protokolle. Unter Windows muss Microsoft Edge WebView2 Runtime installiert sein."
        : "XE could not start. Check the installation, data directory and engine logs. On Windows, Microsoft Edge WebView2 Runtime must be installed.";
    internal static string WebViewDownload => German ? "WebView2 herunterladen" : "Download WebView2";
    internal static string SaveFailed => German ? "Die Auswahl konnte nicht gespeichert werden. Sie gilt nur für dieses Fenster." : "The choice could not be saved. It applies only to this window.";
    internal static string EngineStopped => German ? "Die Engine wurde unerwartet beendet. XE wird geschlossen. Prüfen Sie vor einem Neustart die Engine-Protokolle." : "The engine stopped unexpectedly. XE will close. Check the engine logs before restarting.";
    internal static string StopFailed => German ? "Die Engine konnte nicht vollständig beendet werden. Prüfen Sie die Prozesse vor einem Neustart." : "The engine could not be fully stopped. Check its processes before restarting.";
}
