namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

using System.Net;
using System.Text;

/// <summary>The outcome of waiting for the single AAD redirect callback.</summary>
internal enum LoopbackCallbackOutcome
{
    /// <summary>The authorization code arrived and <c>state</c> matched.</summary>
    Success,

    /// <summary>The callback's <c>state</c> did not match the one this attempt generated (possible CSRF / stale link).</summary>
    StateMismatch,

    /// <summary>AAD redirected back with an <c>error</c> query parameter instead of a code (e.g. consent denied).</summary>
    AadError,

    /// <summary><c>state</c> matched but no <c>code</c> was present.</summary>
    MissingCode,

    /// <summary>No callback arrived before the timeout / cancellation elapsed.</summary>
    TimedOut
}

/// <summary>
///     One-shot result of <see cref="LoopbackAuthorizationCodeListener.WaitForCallbackAsync" />. Never carries a raw
///     query string — only the specific fields the caller needs, and AAD's error text is truncated to a single line
///     before it is ever logged (RFC 6749 §4.1.2.1 error/error_description are operator-facing diagnostics, not
///     attacker-controlled HTML, but the callback page itself never reflects them regardless — see
///     <see cref="LoopbackAuthorizationCodeListener" /> remarks).
/// </summary>
internal sealed record LoopbackCallbackResult(
    LoopbackCallbackOutcome Outcome,
    string? AuthorizationCode = null,
    string? SanitizedError = null,
    string? SanitizedErrorDescription = null)
{
    public static LoopbackCallbackResult Success(string code)
    {
        return new LoopbackCallbackResult(LoopbackCallbackOutcome.Success, AuthorizationCode: code);
    }

    public static readonly LoopbackCallbackResult StateMismatch = new(LoopbackCallbackOutcome.StateMismatch);
    public static readonly LoopbackCallbackResult MissingCode = new(LoopbackCallbackOutcome.MissingCode);
    public static readonly LoopbackCallbackResult TimedOut = new(LoopbackCallbackOutcome.TimedOut);

    public static LoopbackCallbackResult AadError(string? error, string? errorDescription)
    {
        return new LoopbackCallbackResult(LoopbackCallbackOutcome.AadError, SanitizedError: error, SanitizedErrorDescription: errorDescription);
    }
}

/// <summary>
///     A one-shot loopback-only HTTP listener for the AAD authorization-code redirect callback (RFC 8252 §7.3: a
///     native app's redirect URI must be a loopback interface). Bound only when a sign-in starts and stopped
///     immediately after the single callback or a timeout — never left listening between sign-ins. The response page
///     is a FIXED static string; it never reflects any query-parameter content back into the response (an attacker
///     who can make the operator's browser hit this loopback port with a crafted query string must not be able to
///     inject markup/script into the page it returns).
/// </summary>
internal sealed class LoopbackAuthorizationCodeListener : IDisposable
{
    private const string CallbackPageHtml =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>Sign-in complete</title></head>" +
        "<body>Sign-in complete — you can close this tab.</body></html>";

    private readonly HttpListener _listener;

    private LoopbackAuthorizationCodeListener(HttpListener listener)
    {
        _listener = listener;
    }

    /// <summary>
    ///     Starts listening on <paramref name="redirectUri" />. Throws <see cref="ArgumentException" /> when the URI
    ///     is not an absolute http(s) URI on a loopback host — callers must validate with
    ///     <see cref="EntraAuthCodeDefaults.TryValidateRedirectUri" /> before reaching here, this is defense in depth.
    /// </summary>
    public static LoopbackAuthorizationCodeListener Start(Uri redirectUri)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);
        if (!redirectUri.IsAbsoluteUri
            || (redirectUri.Scheme != Uri.UriSchemeHttp && redirectUri.Scheme != Uri.UriSchemeHttps)
            || !EntraAuthCodeDefaults.IsLoopbackHost(redirectUri.Host))
        {
            throw new ArgumentException("The redirect URI must be an absolute http(s) URI on a loopback host.", nameof(redirectUri));
        }

        var listener = new HttpListener();
        listener.Prefixes.Add(BuildListenerPrefix(redirectUri));
        listener.Start();
        return new LoopbackAuthorizationCodeListener(listener);
    }

    // HttpListener requires a root-path ("/") prefix ending in "/" — that trailing slash is the listener's URI
    // shape requirement, not a filesystem path, so the analyzer's hardcoded-path-delimiter flag (S1075) is a false
    // positive here (mirrors FakeOllamaServer's loopback-bind suppression).
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1075:URIs should not be hardcoded",
        Justification = "The trailing '/' is HttpListener's required prefix shape, not a filesystem or network path.")]
    private static string BuildListenerPrefix(Uri redirectUri)
    {
        return redirectUri.GetLeftPart(UriPartial.Authority) + "/";
    }

    /// <summary>
    ///     Waits for the single redirect callback, validates <paramref name="expectedState" />, and always writes the
    ///     fixed static response page before returning (so the operator's browser tab shows completion regardless of
    ///     outcome). Never throws on a malformed/missing/mismatched callback — those map to a non-success outcome.
    /// </summary>
    public async Task<LoopbackCallbackResult> WaitForCallbackAsync(string expectedState, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedState);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        // HttpListener.GetContextAsync() has no cancellation overload, so a timeout/cancellation abandons the
        // pending task rather than cancelling it — it completes later (with a context, or faults once Stop()/
        // Dispose() runs) with nobody left awaiting it. The continuation below observes that eventual outcome so it
        // never surfaces as an unobserved-task-exception, without blocking this method's own return.
        var pendingContext = _listener.GetContextAsync();
        _ = pendingContext.ContinueWith(static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        HttpListenerContext context;
        try
        {
            context = await pendingContext.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return LoopbackCallbackResult.TimedOut;
        }

        var query = context.Request.QueryString;
        var code = query["code"];
        var state = query["state"];
        var error = query["error"];
        var errorDescription = query["error_description"];

        await WriteStaticResponseAsync(context.Response).ConfigureAwait(false);

        if (!string.Equals(state, expectedState, StringComparison.Ordinal))
        {
            return LoopbackCallbackResult.StateMismatch;
        }

        if (!string.IsNullOrEmpty(error))
        {
            return LoopbackCallbackResult.AadError(SanitizeSingleLine(error), SanitizeSingleLine(errorDescription));
        }

        return string.IsNullOrEmpty(code) ? LoopbackCallbackResult.MissingCode : LoopbackCallbackResult.Success(code);
    }

    private static async Task WriteStaticResponseAsync(HttpListenerResponse response)
    {
        var buffer = Encoding.UTF8.GetBytes(CallbackPageHtml);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        try
        {
            await response.OutputStream.WriteAsync(buffer).ConfigureAwait(false);
        }
        finally
        {
            response.OutputStream.Close();
        }
    }

    // Truncates and strips line breaks from an AAD-supplied error/error_description query value before it is ever
    // logged, so a crafted redirect (an operator pasting a malicious authorize URL, or a compromised gateway) cannot
    // inject multi-line or oversized content into the server log.
    private static string? SanitizeSingleLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ');
        return singleLine.Length > 200 ? singleLine[..200] : singleLine;
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
        }
        catch (ObjectDisposedException)
        {
            // Already stopped/disposed.
        }

        ((IDisposable)_listener).Dispose();
    }
}
