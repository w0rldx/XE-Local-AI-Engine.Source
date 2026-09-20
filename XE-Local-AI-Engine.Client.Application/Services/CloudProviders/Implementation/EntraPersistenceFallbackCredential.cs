namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using Azure.Core;
using Azure.Identity;

/// <summary>
///     Wraps a public-client Entra ID <see cref="TokenCredential" /> (device-code / interactive-browser) so an
///     unavailable OS-native token-cache persistence layer does not fail the sign-in.
/// </summary>
/// <remarks>
///     A <see cref="CredentialUnavailableException" /> from that layer (no libsecret on Linux) is caught on first use,
///     logged once, and retried against a rebuilt credential with persistence disabled — an in-memory-only cache,
///     never unencrypted on disk. Subsequent calls go straight to whichever credential is currently active; the
///     decision is made at most once per instance.
/// </remarks>
internal sealed class EntraPersistenceFallbackCredential : TokenCredential
{
    private readonly Func<TokenCachePersistenceOptions?, TokenCredential> _build;
    private readonly Lock _gate = new();
    private readonly ILogger _logger;

    private TokenCredential _current;
    private bool _fellBack;

    public EntraPersistenceFallbackCredential(Func<TokenCachePersistenceOptions?, TokenCredential> build,
        TokenCachePersistenceOptions initialCacheOptions,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(initialCacheOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _build = build;
        _logger = logger;
        _current = build(initialCacheOptions);
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        try
        {
            return _current.GetToken(requestContext, cancellationToken);
        }
        catch (CredentialUnavailableException exception)
        {
            FallBackToInMemory(exception);
            return _current.GetToken(requestContext, cancellationToken);
        }
    }

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        try
        {
            return await _current.GetTokenAsync(requestContext, cancellationToken);
        }
        catch (CredentialUnavailableException exception)
        {
            FallBackToInMemory(exception);
            return await _current.GetTokenAsync(requestContext, cancellationToken);
        }
    }

    private void FallBackToInMemory(Exception exception)
    {
        lock (_gate)
        {
            if (_fellBack)
            {
                return;
            }

            _logger.LogWarning(exception,
                "Encrypted Entra ID token-cache persistence is unavailable on this platform; falling back to an " +
                "in-memory (non-persisted) token cache. Interactive sign-in will be required again after restart.");
            _current = _build(null);
            _fellBack = true;
        }
    }
}
