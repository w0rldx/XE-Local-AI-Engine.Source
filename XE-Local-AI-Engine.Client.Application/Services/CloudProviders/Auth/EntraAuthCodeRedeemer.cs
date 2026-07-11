namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

using XE_Local_AI_Engine.Providers.Abstractions;

/// <inheritdoc cref="IEntraAuthCodeRedeemer" />
public sealed class EntraAuthCodeRedeemer : IEntraAuthCodeRedeemer
{
    private readonly INodeDataDirectory _dataDirectory;
    private readonly ILogger<EntraAuthCodeRedeemer> _logger;

    public EntraAuthCodeRedeemer(INodeDataDirectory dataDirectory, ILogger<EntraAuthCodeRedeemer> logger)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        _dataDirectory = dataDirectory;
        _logger = logger;
    }

    public async Task<EntraAuthCodeRedemptionResult> RedeemAsync(StoredAzureFoundryConnection connection,
        string authorizationCode,
        string codeVerifier,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);

        var app = EntraAuthCodeConfidentialClientFactory.Build(connection.EntraTenantId!, connection.EntraClientId!, connection.EntraClientSecret!, redirectUri);
        await EntraAuthCodeConfidentialClientFactory.TryRegisterPersistentCacheAsync(app, _dataDirectory, _logger).ConfigureAwait(false);

        var result = await app.AcquireTokenByAuthorizationCode([connection.EntraTokenScope!], authorizationCode)
                              .WithPkceCodeVerifier(codeVerifier)
                              .ExecuteAsync(cancellationToken)
                              .ConfigureAwait(false);

        return new EntraAuthCodeRedemptionResult(app, result.Account);
    }
}
