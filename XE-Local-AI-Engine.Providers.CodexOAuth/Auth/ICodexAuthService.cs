namespace XE_Local_AI_Engine.Providers.CodexOAuth.Auth;

/// <summary>
///     Contract for the Codex OAuth login / refresh lifecycle.
/// </summary>
public interface ICodexAuthService
{
    /// <summary>
    ///     Starts the interactive PKCE (S256) loopback login: binds the loopback callback listener, builds the
    ///     authorize URL, and begins waiting for the callback in the background. The returned
    ///     <see cref="CodexLoginHandle" /> exposes the authorize URL <em>immediately</em> so the React client can render
    ///     it as a user-clicked link, and a
    ///     <see cref="CodexLoginHandle.Completion" /> task that resolves once the code is exchanged and persisted.
    /// </summary>
    CodexLoginHandle BeginLogin(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Runs the interactive PKCE (S256) login against a loopback callback listener,
    ///     exchanges the authorization code, persists the session, and returns it. Convenience wrapper over
    ///     <see cref="BeginLogin" /> that awaits completion.
    /// </summary>
    Task<CodexTokens> LoginAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Exchanges the current refresh token for a new session (<c>grant_type=refresh_token</c>) and persists it.
    /// </summary>
    Task<CodexTokens> RefreshAsync(CodexTokens current, CancellationToken cancellationToken = default);
}
