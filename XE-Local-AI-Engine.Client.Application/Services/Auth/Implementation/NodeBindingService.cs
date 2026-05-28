namespace XE_Local_AI_Engine.Client.Services.Auth.Implementation;

using XE_Local_AI_Engine.Client.Services.Auth;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models.NodeBinding;

public sealed class NodeBindingService : INodeBindingService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NodeBindingService> _logger;
    private readonly IOptions<CentralPlatformOptions> _platformOptions;
    private readonly ITokenStore _tokenStore;
    private readonly IOptions<WorkerNodeOptions> _workerOptions;
    private CancellationTokenSource? _pollingCancellation;

    public NodeBindingService(IHttpClientFactory httpClientFactory,
        ITokenStore tokenStore,
        IOptions<CentralPlatformOptions> platformOptions,
        IOptions<WorkerNodeOptions> workerOptions,
        ILogger<NodeBindingService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _platformOptions = platformOptions ?? throw new ArgumentNullException(nameof(platformOptions));
        _workerOptions = workerOptions ?? throw new ArgumentNullException(nameof(workerOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<NodeBindingSession> StartBindingAsync(CancellationToken cancellationToken = default)
    {
        await CancelAsync().ConfigureAwait(false);
        _pollingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var request = new StartNodeBindingRequest
        {
            NodeName = _workerOptions.Value.NodeName,
            LocalMachineId = Environment.MachineName
        };

        using var client = _httpClientFactory.CreateClient("CentralPlatformApi");
        using var response = await client.PostAsJsonAsync(_platformOptions.Value.DeviceBindingStartEndpoint,
            request,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateNodeBindingExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        }

        var startResponse = await response.Content.ReadFromJsonAsync<StartNodeBindingResponse>(SerializerOptions, cancellationToken)
                                          .ConfigureAwait(false)
                            ?? throw new NodeBindingException("Central Platform returned an empty device binding response.");

        _logger.LogInformation("Started device binding for worker node {NodeName}.", request.NodeName);

        return new NodeBindingSession
        {
            DeviceCode = startResponse.DeviceCode,
            UserCode = startResponse.UserCode,
            VerificationUri = startResponse.VerificationUri,
            VerificationUriComplete = startResponse.VerificationUriComplete,
            ExpiresAt = startResponse.ExpiresAt,
            IntervalSeconds = startResponse.IntervalSeconds,
            Status = NodeBindingStatus.Pending
        };
    }

    public async Task<PollNodeBindingResponse> PollUntilTerminalAsync(NodeBindingSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            _pollingCancellation?.Token ?? CancellationToken.None);
        var linkedToken = linkedCancellationTokenSource.Token;
        while (true)
        {
            linkedToken.ThrowIfCancellationRequested();
            var pollResponse = await PollOnceAsync(session.DeviceCode, linkedToken).ConfigureAwait(false);
            var status = ParseStatus(pollResponse.Status);

            if (status is NodeBindingStatus.Approved)
            {
                if (pollResponse.Credentials is null)
                {
                    throw new NodeBindingException("Central Platform approved binding without returning worker credentials.");
                }

                await _tokenStore.StoreTokensAsync(pollResponse.Credentials,
                    new TokenStoreMetadata
                    {
                        BindingMethod = "device-code",
                        AutoConnectOnStart = false,
                        LastKnownNodeName = _workerOptions.Value.NodeName
                    }).ConfigureAwait(false);

                _logger.LogInformation("Device binding approved for worker node {NodeName} and credentials stored.", _workerOptions.Value.NodeName);
                return pollResponse;
            }

            if (status is NodeBindingStatus.Expired or NodeBindingStatus.Denied or NodeBindingStatus.Consumed or NodeBindingStatus.Failed)
            {
                return pollResponse;
            }

            var interval = NormalizeInterval(pollResponse.IntervalSeconds == 0 ? session.IntervalSeconds : pollResponse.IntervalSeconds);
            await Task.Delay(interval, linkedToken).ConfigureAwait(false);
        }
    }

    public async Task CancelAsync()
    {
        if (_pollingCancellation is null)
        {
            return;
        }

        await _pollingCancellation.CancelAsync().ConfigureAwait(false);
        _pollingCancellation.Dispose();
        _pollingCancellation = null;
    }

    public async ValueTask DisposeAsync()
    {
        await CancelAsync().ConfigureAwait(false);
    }

    private async Task<PollNodeBindingResponse> PollOnceAsync(string deviceCode, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient("CentralPlatformApi");
        using var response = await client.PostAsJsonAsync(_platformOptions.Value.DeviceBindingTokenEndpoint,
            new PollNodeBindingRequest
            {
                DeviceCode = deviceCode
            },
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateNodeBindingExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        }

        return await response.Content.ReadFromJsonAsync<PollNodeBindingResponse>(SerializerOptions, cancellationToken)
                             .ConfigureAwait(false)
               ?? throw new NodeBindingException("Central Platform returned an empty device binding poll response.");
    }

    private static TimeSpan NormalizeInterval(int intervalSeconds)
    {
        return TimeSpan.FromSeconds(Math.Clamp(intervalSeconds, 1, 300));
    }

    private static NodeBindingStatus ParseStatus(string status)
    {
        return status.Trim().ToUpperInvariant() switch
        {
            "PENDING" => NodeBindingStatus.Pending,
            "APPROVED" => NodeBindingStatus.Approved,
            "CONSUMED" => NodeBindingStatus.Consumed,
            "EXPIRED" => NodeBindingStatus.Expired,
            "DENIED" => NodeBindingStatus.Denied,
            _ => NodeBindingStatus.Failed
        };
    }

    private static async Task<NodeBindingException> CreateNodeBindingExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var message = string.IsNullOrWhiteSpace(errorBody)
            ? $"Device binding failed with status code {(int)response.StatusCode}."
            : $"Device binding failed: {errorBody}";

        return response.StatusCode switch
        {
            HttpStatusCode.BadRequest => new NodeBindingException("Device binding request was rejected by the Central Platform."),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new NodeBindingException("Device binding code is invalid."),
            HttpStatusCode.Gone => new NodeBindingException("Device binding request has expired."),
            _ => new NodeBindingException(message)
        };
    }
}
