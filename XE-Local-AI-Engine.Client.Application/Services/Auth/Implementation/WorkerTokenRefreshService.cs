namespace XE_Local_AI_Engine.Client.Services.Auth.Implementation;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Application service for worker token refresh behavior.
/// </summary>
public sealed class WorkerTokenRefreshService : IWorkerTokenRefreshService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WorkerTokenRefreshService> _logger;
    private readonly IOptions<CentralPlatformOptions> _platformOptions;
    private readonly ITokenStore _tokenStore;

    public WorkerTokenRefreshService(IHttpClientFactory httpClientFactory,
        ITokenStore tokenStore,
        IOptions<CentralPlatformOptions> platformOptions,
        ILogger<WorkerTokenRefreshService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _platformOptions = platformOptions ?? throw new ArgumentNullException(nameof(platformOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<WorkerTokenRefreshOutcome> TryRefreshAsync(CancellationToken cancellationToken = default)
    {
        var clientNodeId = await _tokenStore.GetClientNodeIdAsync().ConfigureAwait(false);
        var refreshToken = await _tokenStore.GetRefreshTokenAsync().ConfigureAwait(false);
        if (clientNodeId is null || string.IsNullOrWhiteSpace(refreshToken))
        {
            _logger.LogWarning("Worker credentials cannot be refreshed because refresh metadata is missing. Re-pairing is required.");
            return WorkerTokenRefreshOutcome.CredentialsRevoked;
        }

        HttpResponseMessage response;
        try
        {
            using var client = _httpClientFactory.CreateClient("CentralPlatformApi");
            response = await client.PostAsJsonAsync(_platformOptions.Value.WorkerTokenRefreshEndpoint,
                new RefreshWorkerTokenRequest
                {
                    ClientNodeId = clientNodeId.Value,
                    RefreshToken = refreshToken
                },
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Worker refresh token request failed with a network error. Treating as transient.");
            return WorkerTokenRefreshOutcome.TransientFailure;
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Worker refresh token was rejected by the Central Platform. Clearing local worker credentials.");
                await _tokenStore.ClearTokensAsync().ConfigureAwait(false);
                return WorkerTokenRefreshOutcome.CredentialsRevoked;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Worker refresh token request failed with status code {StatusCode}. Treating as transient.", response.StatusCode);
                return WorkerTokenRefreshOutcome.TransientFailure;
            }

            var credentials = await response.Content.ReadFromJsonAsync<PairClientResponse>(SerializerOptions, cancellationToken).ConfigureAwait(false);
            if (credentials is null)
            {
                _logger.LogWarning("Worker refresh token request returned an empty response. Treating as transient.");
                return WorkerTokenRefreshOutcome.TransientFailure;
            }

            await _tokenStore.StoreTokensAsync(credentials).ConfigureAwait(false);
            _logger.LogInformation("Worker credentials refreshed for client node {ClientNodeId}.", credentials.ClientNodeId);
            return WorkerTokenRefreshOutcome.Success;
        }
    }
}
