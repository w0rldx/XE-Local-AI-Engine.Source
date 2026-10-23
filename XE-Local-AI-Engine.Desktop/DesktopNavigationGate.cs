namespace XE_Local_AI_Engine.Desktop;

/// <summary>Decides what a failed main-frame navigation means while the start page is loading. UI thread only.</summary>
/// <remarks>
///     The WebView wrapper reports only <c>IsSuccess</c>, never WebView2's error status, so a navigation the SPA replaced
///     mid-load (an auth redirect) looks like a load failure. One with a newer navigation still in flight is ignored; a
///     failure with nothing newer in flight retries the start page until <see cref="RetryWindow" /> after the first
///     failure has passed: after a vault unlock the engine host listens a few seconds after the unlock page let go of the port.
/// </remarks>
internal sealed class DesktopNavigationGate
{
    /// <summary>How long failed start-page loads are retried; well inside the window's 30-second readiness deadline.</summary>
    internal static readonly TimeSpan RetryWindow = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _time;
    private int _pending;
    private DateTimeOffset? _firstFailure;

    internal DesktopNavigationGate(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    internal enum Outcome
    {
        Loaded,
        Superseded,
        Retry,
        Failed
    }

    /// <summary>Counts every started navigation, a blocked one too, because its completion is reported as well.</summary>
    internal void Started() =>
        _pending++;

    /// <summary>A new start-page load (the first one or a reload after an engine relaunch) gets the whole retry window.</summary>
    internal void Restart() =>
        _firstFailure = null;

    /// <summary>Whether a failed start page may still be tried again.</summary>
    internal bool CanRetry() =>
        _firstFailure is not { } first || _time.GetUtcNow() - first < RetryWindow;

    internal Outcome Completed(bool success)
    {
        _pending = Math.Max(0, _pending - 1);
        if (success)
        {
            _firstFailure = null;
            return Outcome.Loaded;
        }

        // A navigation that never reports its completion only costs the window its 30-second readiness deadline.
        if (_pending > 0) { return Outcome.Superseded; }

        _firstFailure ??= _time.GetUtcNow();
        return CanRetry() ? Outcome.Retry : Outcome.Failed;
    }
}
