namespace XE_Local_AI_Engine.Client.Services.Auth.Implementation;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;

public sealed class PairingService : IPairingService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PairingService> _logger;
    private readonly IOptions<CentralPlatformOptions> _platformOptions;
    private readonly ITokenStore _tokenStore;
    private readonly IOptions<WorkerNodeOptions> _workerOptions;

    public PairingService(IHttpClientFactory httpClientFactory,
        ITokenStore tokenStore,
        IOptions<CentralPlatformOptions> platformOptions,
        IOptions<WorkerNodeOptions> workerOptions,
        ILogger<PairingService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _platformOptions = platformOptions ?? throw new ArgumentNullException(nameof(platformOptions));
        _workerOptions = workerOptions ?? throw new ArgumentNullException(nameof(workerOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<PairClientResponse> PairAsync(string pairingToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingToken);

        var request = new PairClientRequest
        {
            Token = pairingToken.Trim(),
            NodeName = _workerOptions.Value.NodeName
        };

        using var client = _httpClientFactory.CreateClient("CentralPlatformApi");
        using var response = await client.PostAsJsonAsync(_platformOptions.Value.PairingEndpoint,
            request,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreatePairingExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        }

        var pairingResponse = await response.Content.ReadFromJsonAsync<PairClientResponse>(SerializerOptions, cancellationToken)
                                            .ConfigureAwait(false);

        if (pairingResponse is null)
        {
            throw new PairingException("Central Platform returned an empty pairing response.");
        }

        await _tokenStore.StoreTokensAsync(pairingResponse).ConfigureAwait(false);

        _logger.LogInformation("Worker node {NodeName} paired successfully with client node id {ClientNodeId}.",
            request.NodeName,
            pairingResponse.ClientNodeId);

        return pairingResponse;
    }

    public async Task UnpairAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _tokenStore.ClearTokensAsync().ConfigureAwait(false);
        _logger.LogInformation("Worker node credentials cleared locally.");
    }

    private static async Task<PairingException> CreatePairingExceptionAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var message = string.IsNullOrWhiteSpace(errorBody)
            ? $"Pairing failed with status code {(int)response.StatusCode}."
            : $"Pairing failed: {errorBody}";

        return response.StatusCode switch
        {
            HttpStatusCode.BadRequest => CreateBadRequestException(errorBody),
            HttpStatusCode.Conflict => new PairingTokenUsedException(message),
            HttpStatusCode.Gone => new PairingTokenExpiredException(message),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new PairingTokenInvalidException(message),
            _ => new PairingException(message)
        };
    }

    private static PairingException CreateBadRequestException(string errorBody)
    {
        if (errorBody.Contains("expired", StringComparison.OrdinalIgnoreCase))
        {
            return new PairingTokenExpiredException("Pairing token has expired.");
        }

        if (errorBody.Contains("used", StringComparison.OrdinalIgnoreCase))
        {
            return new PairingTokenUsedException("Pairing token has already been used.");
        }

        if (errorBody.Contains("invalid", StringComparison.OrdinalIgnoreCase))
        {
            return new PairingTokenInvalidException("Pairing token is invalid.");
        }

        return new PairingException(string.IsNullOrWhiteSpace(errorBody)
            ? "Pairing request was rejected by the Central Platform."
            : $"Pairing failed: {errorBody}");
    }
}
