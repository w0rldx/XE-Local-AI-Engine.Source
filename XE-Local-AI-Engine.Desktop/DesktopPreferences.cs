namespace XE_Local_AI_Engine.Desktop;

internal enum DesktopCloseAction
{
    Ask,
    Tray,
    Quit
}

internal static class DesktopPreferences
{
    internal static DesktopCloseAction Resolve(DesktopCloseAction preference, bool trayAvailable) =>
        preference == DesktopCloseAction.Tray && !trayAvailable ? DesktopCloseAction.Ask : preference;

    internal static async Task<DesktopCloseAction> ReadAsync(string directory, CancellationToken cancellationToken) =>
        await ReadTextAsync(directory, "desktop-close.txt", cancellationToken) switch
        {
            "tray" => DesktopCloseAction.Tray,
            "quit" => DesktopCloseAction.Quit,
            _ => DesktopCloseAction.Ask
        };

    internal static Task WriteAsync(string directory, DesktopCloseAction action, CancellationToken cancellationToken) =>
        WriteTextAsync(directory, "desktop-close.txt", action switch
        {
            DesktopCloseAction.Tray => "tray",
            DesktopCloseAction.Quit => "quit",
            _ => "ask"
        }, cancellationToken);

    internal static Task<string?> ReadLanguageAsync(string directory, CancellationToken cancellationToken) =>
        ReadTextAsync(directory, "desktop-language.txt", cancellationToken);

    internal static Task WriteLanguageAsync(string directory, string language, CancellationToken cancellationToken) =>
        WriteTextAsync(directory, "desktop-language.txt", language, cancellationToken);

    /// <summary>The overlay chord the operator wrote into desktop-hotkey.txt; the default when absent or unreadable.</summary>
    internal static async Task<DesktopHotkey> ReadHotkeyAsync(string directory, CancellationToken cancellationToken) =>
        DesktopHotkey.ParseOrDefault(await ReadTextAsync(directory, "desktop-hotkey.txt", cancellationToken));

    internal static async Task<DesktopOverlayBounds?> ReadOverlayBoundsAsync(string directory, CancellationToken cancellationToken) =>
        DesktopOverlayBounds.Parse(await ReadTextAsync(directory, "desktop-overlay.txt", cancellationToken));

    internal static Task WriteOverlayBoundsAsync(string directory, DesktopOverlayBounds bounds, CancellationToken cancellationToken) =>
        WriteTextAsync(directory, "desktop-overlay.txt", bounds.ToString(), cancellationToken);

    private static async Task<string?> ReadTextAsync(string directory, string name, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(Path.Combine(directory, name), cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task WriteTextAsync(string directory, string name, string value, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, name);
        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, value, cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
    }
}
