namespace XE_Local_AI_Engine.Client.Services.Auth;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;

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

    public async Task<bool> TryRefreshAsync(CancellationToken cancellationToken = default)
    {
        var clientNodeId = await _tokenStore.GetClientNodeIdAsync().ConfigureAwait(false);
        var refreshToken = await _tokenStore.GetRefreshTokenAsync().ConfigureAwait(false);
        if (clientNodeId is null || string.IsNullOrWhiteSpace(refreshToken))
        {
            _logger.LogInformation("Worker credentials cannot be refreshed because refresh metadata is missing.");
            return false;
        }

        using var client = _httpClientFactory.CreateClient("CentralPlatformApi");
        using var response = await client.PostAsJsonAsync(_platformOptions.Value.WorkerTokenRefreshEndpoint,
            new RefreshWorkerTokenRequest
            {
                ClientNodeId = clientNodeId.Value,
                RefreshToken = refreshToken
            },
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Worker refresh token was rejected by the Central Platform. Clearing local worker credentials.");
            await _tokenStore.ClearTokensAsync().ConfigureAwait(false);
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Worker refresh token request failed with status code {StatusCode}.", response.StatusCode);
            return false;
        }

        var credentials = await response.Content.ReadFromJsonAsync<PairClientResponse>(SerializerOptions, cancellationToken).ConfigureAwait(false);
        if (credentials is null)
        {
            _logger.LogWarning("Worker refresh token request returned an empty response.");
            return false;
        }

        await _tokenStore.StoreTokensAsync(credentials).ConfigureAwait(false);
        _logger.LogInformation("Worker credentials refreshed for client node {ClientNodeId}.", credentials.ClientNodeId);
        return true;
    }
}
