namespace XE_Local_AI_Engine.Providers.CodexOAuth.Auth;

using Microsoft.Extensions.Logging;

/// <summary>The state of the most recent / current Codex login attempt, surfaced by the <c>codex/status</c> endpoint.</summary>
public enum CodexLoginState
{
    /// <summary>No login has been started this process lifetime.</summary>
    None,

    /// <summary>A login is in flight: the authorize URL is available and the loopback callback is awaited.</summary>
    Pending,

    /// <summary>The most recent login completed and persisted a session.</summary>
    Succeeded,

    /// <summary>The most recent login failed (timed out, was superseded, or the exchange errored).</summary>
    Failed
}

/// <summary>
///     An immutable snapshot of the current login state for the status endpoint. Carries no token material.
/// </summary>
/// <param name="State">Current login lifecycle state.</param>
/// <param name="AuthorizeUrl">The authorize URL while <see cref="CodexLoginState.Pending" />; otherwise null.</param>
public sealed record CodexLoginStatus(CodexLoginState State, Uri? AuthorizeUrl)
{
    /// <summary>Idle status used before any login has been attempted.</summary>
    public static CodexLoginStatus None { get; } = new(CodexLoginState.None, AuthorizeUrl: null);
}

/// <summary>
///     Owns the pending-login lifecycle so the Operator endpoints can start a loopback PKCE login, return the
///     authorize URL immediately, and poll status until it completes. A second <see cref="Start" />
///     <em>supersedes</em> any in-flight login: the prior attempt is cancelled and its loopback listener freed,
///     so the new login can re-bind the callback port. Never logs token material.
/// </summary>
public sealed class CodexLoginCoordinator : ICodexLoginCoordinator, IDisposable
{
    private readonly Lazy<ICodexAuthService> _authService;
    private readonly Lock _gate = new();
    private readonly ILogger<CodexLoginCoordinator> _logger;
    private readonly Action? _onLoginSucceeded;

    private CancellationTokenSource? _pendingCts;
    private CodexLoginStatus _status = CodexLoginStatus.None;

    /// <summary>
    ///     Takes the auth service as a <see cref="Lazy{T}" /> so its transport (an <see cref="System.Net.Http.HttpClient" />)
    ///     is built only on first <see cref="Start" />, not when this singleton is constructed. This keeps endpoint
    ///     instantiation at host startup from eagerly materializing the auth HttpClient.
    /// </summary>
    /// <param name="onLoginSucceeded">
    ///     Optional callback invoked once a login completes and a session is persisted (the background exchange's
    ///     success path). The host wires this to invalidate the active-cloud selection snapshot so a sign-in takes
    ///     effect on the very next send — the provider layer stays decoupled from the Application-layer selector.
    /// </param>
    public CodexLoginCoordinator(Lazy<ICodexAuthService> authService,
        ILogger<CodexLoginCoordinator> logger,
        Action? onLoginSucceeded = null)
    {
        ArgumentNullException.ThrowIfNull(authService);
        ArgumentNullException.ThrowIfNull(logger);

        _authService = authService;
        _logger = logger;
        _onLoginSucceeded = onLoginSucceeded;
    }

    /// <summary>
    ///     Starts (or supersedes) a loopback PKCE login and returns the authorize URL once the listener is bound.
    ///     The token exchange continues in the background; callers poll <see cref="GetStatus" /> for completion.
    /// </summary>
    public Uri Start()
    {
        CancellationTokenSource newCts;
        CancellationTokenSource? superseded;

        lock (_gate)
        {
            // Supersede any in-flight login so the new attempt can re-bind the loopback callback port.
            superseded = _pendingCts;
            newCts = new CancellationTokenSource();
            _pendingCts = newCts;
        }

        // Cancel the prior attempt outside the lock; its listener is freed in CompleteLoginAsync's finally and the
        // CTS is disposed by that attempt's own TrackCompletionAsync — we only signal cancellation here.
        if (superseded is not null)
        {
            _logger.LogInformation("Superseding an in-flight Codex login with a new login request.");
            CancelPending(superseded);
        }

        var handle = _authService.Value.BeginLogin(newCts.Token);

        lock (_gate)
        {
            // Only adopt this attempt's status if it is still the current one (not already superseded again).
            if (ReferenceEquals(_pendingCts, newCts))
            {
                _status = new CodexLoginStatus(CodexLoginState.Pending, handle.AuthorizeUrl);
            }
        }

        // Observe completion to flip the status; failures here are expected (timeout/supersede) and not rethrown.
        _ = TrackCompletionAsync(handle, newCts);

        return handle.AuthorizeUrl;
    }

    /// <summary>Returns the current login status snapshot (no token material).</summary>
    public CodexLoginStatus GetStatus()
    {
        lock (_gate)
        {
            return _status;
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? pending;
        lock (_gate)
        {
            pending = _pendingCts;
            _pendingCts = null;
        }

        if (pending is not null)
        {
            CancelPending(pending);
        }
    }

    private async Task TrackCompletionAsync(CodexLoginHandle handle, CancellationTokenSource cts)
    {
        try
        {
            await handle.Completion.ConfigureAwait(false);
            if (UpdateStatusIfCurrent(cts, new CodexLoginStatus(CodexLoginState.Succeeded, AuthorizeUrl: null)))
            {
                // Session persisted: notify the host so the active-cloud selector re-reads and routes the next
                // send to Codex immediately (not after the snapshot TTL). Best-effort; never break login on it.
                try
                {
                    _onLoginSucceeded?.Invoke();
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Codex post-login selection-cache invalidation failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timed out or superseded — a superseding Start (if any) already set its own pending status,
            // and UpdateStatusIfCurrent ensures we do not clobber it.
            UpdateStatusIfCurrent(cts, new CodexLoginStatus(CodexLoginState.Failed, AuthorizeUrl: null));
        }
        catch (Exception exception)
        {
            // Never log token material; CodexAuthException messages are already redacted.
            _logger.LogWarning(exception, "Codex login did not complete successfully.");
            UpdateStatusIfCurrent(cts, new CodexLoginStatus(CodexLoginState.Failed, AuthorizeUrl: null));
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pendingCts, cts))
                {
                    _pendingCts = null;
                }
            }

            cts.Dispose();
        }
    }

    /// <summary>
    ///     Sets <paramref name="status" /> only when <paramref name="cts" /> is still the current attempt (a superseded
    ///     attempt must not clobber the new one). Returns whether the status was applied.
    /// </summary>
    private bool UpdateStatusIfCurrent(CancellationTokenSource cts, CodexLoginStatus status)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_pendingCts, cts))
            {
                return false;
            }

            _status = status;
            return true;
        }
    }

    private static void CancelPending(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already completed and disposed by its own tracking task; nothing to cancel.
        }
    }
}
