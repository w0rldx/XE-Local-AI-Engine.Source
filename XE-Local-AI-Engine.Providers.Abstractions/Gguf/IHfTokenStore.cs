namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Persistence boundary for the optional Hugging Face access token used to download gated repos. The token is a
///     secret: it is stored encrypted at rest and is exposed only to the download client, which sets it as an
///     <c>Authorization: Bearer</c> header at request time. It is never logged, never placed in exceptions, never in the
///     registry manifest, and never indexed.
/// </summary>
public interface IHfTokenStore
{
    /// <summary>Loads the stored token, or <see langword="null" /> when none is configured (anonymous, public-only).</summary>
    Task<string?> GetTokenAsync(CancellationToken ct);

    /// <summary>Persists <paramref name="token" />, encrypted at rest, replacing any existing token.</summary>
    Task SetTokenAsync(string token, CancellationToken ct);

    /// <summary>Clears any stored token (returns to anonymous access).</summary>
    Task ClearTokenAsync(CancellationToken ct);

    /// <summary>Returns whether a token is currently configured, without exposing its value.</summary>
    Task<bool> HasTokenAsync(CancellationToken ct);
}
