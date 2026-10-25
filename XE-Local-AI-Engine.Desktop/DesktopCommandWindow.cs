namespace XE_Local_AI_Engine.Desktop;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

/// <summary>The small always-on-top command overlay on the engine's <see cref="Route" /> page (ADR 0013, 2026-10-15).</summary>
/// <remarks>It is a <see cref="DesktopWindow" />, so origin, profile, document policy and navigation guard are the main
///     window's. Escape, focus loss and closing only hide it; <see cref="DisposeAsync" /> is the one real close.</remarks>
internal sealed class DesktopCommandWindow : IAsyncDisposable
{
    /// <summary>The SPA route the overlay opens; a protocol literal duplicated from the React route tree.</summary>
    internal const string Route = "/command";

    /// <summary>The SPA's sign-in route; a protocol literal duplicated from the React route tree.</summary>
    internal const string SignInRoute = "/login";

    private readonly DesktopWindow _window;
    private readonly Uri _start;
    private readonly Action<DesktopOverlayBounds> _hidden;
    private readonly Action _shown;
    private readonly Func<Task<Uri?>> _mainLocation;
    private bool _visible;
    private bool _closing;

    /// <param name="mainLocation">Reads the main window's address; never throws, null when unknown.</param>
    internal DesktopCommandWindow(DesktopLaunchOptions options,
        bool devTools,
        DesktopOverlayBounds? bounds,
        Action shown,
        Action<DesktopOverlayBounds> hidden,
        Func<Task<Uri?>> mainLocation)
    {
        _shown = shown;
        _mainLocation = mainLocation;
        _start = new Uri(options.Origin, Route);
        _hidden = hidden;
        // A borderless window has no title bar, so this strip is the only place to drag it from.
        var grip = new Border
        {
            Height = 8,
            Background = Brushes.Gray,
            Cursor = new Cursor(StandardCursorType.SizeAll)
        };
        _window = new DesktopWindow(options, devTools, Route, grip)
        {
            Topmost = true,
            WindowDecorations = WindowDecorations.None,
            ShowInTaskbar = false,
            MinWidth = DesktopOverlayBounds.MinWidth,
            MinHeight = DesktopOverlayBounds.MinHeight,
            Width = bounds?.Width ?? DesktopOverlayBounds.DefaultWidth,
            Height = bounds?.Height ?? DesktopOverlayBounds.DefaultHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        grip.PointerPressed += (_, args) => _window.BeginMoveDrag(args);
        if (bounds is not null && _window.Screens.ScreenFromPoint(new PixelPoint(bounds.X, bounds.Y)) is not null)
        {
            _window.WindowStartupLocation = WindowStartupLocation.Manual;
            _window.Position = new PixelPoint(bounds.X, bounds.Y);
        }

        // Tunnel and handled-too: Avalonia sees the key only while focus is outside the native page; Windows also
        // claims Escape through the shell's hotkey while the overlay shows.
        _window.AddHandler(InputElement.KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.Escape) { Hide(); }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        // A dialog the page opens (a microphone consent) takes focus without the overlay being dismissed.
        _window.Deactivated += (_, _) =>
        {
            if (_window.OwnedWindows.Count == 0) { Hide(); }
        };
        _window.Closing += (_, args) =>
        {
            if (_closing) { return; }

            args.Cancel = true;
            Hide();
        };
        _ = RecordLoadFailureAsync();
    }

    /// <summary>The pure show/hide decision: the hotkey toggles, a menu entry always shows.</summary>
    internal static bool ShouldShow(bool visible, bool toggle) =>
        !(visible && toggle);

    /// <summary>Whether a shown overlay must load its route again: the page left it, or the main window is on sign-in.</summary>
    /// <remarks>The page leaves the route when a sign-in page opened before the main window signed in. A main window on
    ///     sign-in signed out, which ended the overlay's session too (the windows share one). Nothing known keeps the page.</remarks>
    internal static bool ShouldReloadOnShow(Uri? current, Uri start, Uri? main) =>
        (current is not null && !string.Equals(current.AbsolutePath, start.AbsolutePath, StringComparison.Ordinal))
        || string.Equals(main?.AbsolutePath, SignInRoute, StringComparison.Ordinal);

    internal void Show(bool toggle)
    {
        if (!ShouldShow(_visible, toggle))
        {
            Hide();
            return;
        }

        _visible = true;
        _window.Show();
        _window.Activate();
        _shown();
        _ = ReturnToRouteAsync();
    }

    internal void Hide()
    {
        if (!_visible) { return; }

        _visible = false;
        var bounds = new DesktopOverlayBounds(_window.Position.X, _window.Position.Y, _window.Width, _window.Height);
        _window.Hide();
        _hidden(bounds);
    }

    public async ValueTask DisposeAsync()
    {
        _closing = true;
        await _window.DisposeAsync();
        _window.Close();
    }

    // Fire-and-forget from Show: the reads never throw and Reload is a no-op once the window is disposed.
    private async Task ReturnToRouteAsync()
    {
        var main = await _mainLocation();
        if (ShouldReloadOnShow(await _window.ReadLocationAsync(CancellationToken.None), _start, main)) { _window.Reload(); }
    }

    private async Task RecordLoadFailureAsync()
    {
        try { await _window.WaitUntilReadyAsync(CancellationToken.None); }
        catch (Exception exception)
        {
            DesktopStartupDiagnostics.Record($"The command overlay page did not become ready ({exception.GetType().Name}). {exception.Message}");
        }
    }
}
