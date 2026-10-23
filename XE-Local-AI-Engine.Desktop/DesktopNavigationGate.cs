namespace XE_Local_AI_Engine.Desktop;

/// <summary>Decides what a failed main-frame navigation means while the window is still loading. UI thread only.</summary>
/// <remarks>
///     The WebView wrapper reports only <c>IsSuccess</c>, never WebView2's error status, so a navigation the SPA replaced
///     mid-load (an auth redirect) looks like a load failure. One with a newer navigation still in flight is ignored; a
///     failure with nothing newer in flight retries the start page once before it is fatal.
/// </remarks>
internal sealed class DesktopNavigationGate
{
    private int _pending;
    private bool _retried;

    internal enum Outcome
    {
        Loaded,
        Superseded,
        Retry,
        Failed
    }

    /// <summary>Counts every started navigation, a blocked one too, because its completion is reported as well.</summary>
    internal void Started() => _pending++;

    internal Outcome Completed(bool success)
    {
        _pending = Math.Max(0, _pending - 1);
        if (success) { return Outcome.Loaded; }

        // A navigation that never reports its completion only costs the window its 30-second readiness deadline.
        if (_pending > 0) { return Outcome.Superseded; }

        if (_retried) { return Outcome.Failed; }

        _retried = true;
        return Outcome.Retry;
    }
}
