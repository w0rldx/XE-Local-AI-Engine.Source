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

    internal static async Task<DesktopCloseAction> ReadAsync(string directory, CancellationToken cancellationToken)
    {
        try
        {
            var text = await File.ReadAllTextAsync(Path.Combine(directory, "desktop-close.txt"), cancellationToken);
            return text switch
            {
                "tray" => DesktopCloseAction.Tray,
                "quit" => DesktopCloseAction.Quit,
                _ => DesktopCloseAction.Ask
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DesktopCloseAction.Ask;
        }
    }

    internal static async Task WriteAsync(string directory, DesktopCloseAction action, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, "desktop-close.txt");
        var temporaryPath = path + ".tmp";
        var value = action switch
        {
            DesktopCloseAction.Tray => "tray",
            DesktopCloseAction.Quit => "quit",
            _ => "ask"
        };
        await File.WriteAllTextAsync(temporaryPath, value, cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
    }
}
