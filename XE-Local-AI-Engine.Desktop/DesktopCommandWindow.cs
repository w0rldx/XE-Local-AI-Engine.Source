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

    private readonly DesktopWindow _window;
    private readonly Action<DesktopOverlayBounds> _hidden;
    private readonly Action _shown;
    private bool _visible;
    private bool _closing;

    internal DesktopCommandWindow(DesktopLaunchOptions options, bool devTools, DesktopOverlayBounds? bounds, Action shown, Action<DesktopOverlayBounds> hidden)
    {
        _shown = shown;
        _hidden = hidden;
        // A borderless window has no title bar, so this strip is the only place to drag it from.
        var grip = new Border { Height = 8, Background = Brushes.Gray, Cursor = new Cursor(StandardCursorType.SizeAll) };
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
    internal static bool ShouldShow(bool visible, bool toggle) => !(visible && toggle);

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

    private async Task RecordLoadFailureAsync()
    {
        try { await _window.WaitUntilReadyAsync(CancellationToken.None); }
        catch (Exception exception)
        {
            DesktopStartupDiagnostics.Record($"The command overlay page did not become ready ({exception.GetType().Name}). {exception.Message}");
        }
    }
}
