namespace XE_Local_AI_Engine.Providers.CodexOAuth.Auth;

/// <summary>
///     Contract for persisting the encrypted Codex OAuth session.
/// </summary>
public interface ICodexTokenStore
{
    /// <summary>Loads the stored session, or <see langword="null" /> if none / undecryptable.</summary>
    Task<CodexTokens?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Encrypts and persists the session with user-only file permissions.</summary>
    Task SaveAsync(CodexTokens tokens, CancellationToken cancellationToken = default);

    /// <summary>Removes any stored session (logout).</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
