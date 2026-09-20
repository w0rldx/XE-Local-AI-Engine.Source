namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Lists the models the local runtime currently holds in memory (RAM/VRAM).
/// </summary>
/// <remarks>
///     On provider-unreachable it returns an OK-empty/unavailable response, never a 500, so the loaded-models page can
///     poll and degrade gracefully — mirroring <see cref="ListLocalModelsEndpoint" />. The response also reports
///     whether the Ollama runtime is configured at all
///     (<see cref="RunningLocalModelsResponse.OllamaConfigured" />), so the client stops polling when it is off.
/// </remarks>
public sealed class GetRunningLocalModelsEndpoint : EndpointWithoutRequest<RunningLocalModelsResponse>
{
    private readonly ILocalModelCatalogService _catalogService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GetRunningLocalModelsEndpoint> _logger;

    public GetRunningLocalModelsEndpoint(
        ILocalModelCatalogService catalogService,
        IConfiguration configuration,
        ILogger<GetRunningLocalModelsEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(catalogService);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        _catalogService = catalogService;
        _configuration = configuration;
        _logger = logger;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.LocalModels.Running);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // The SAME gate AddOllamaRuntime uses to decide whether to register the Ollama provider (enabled unless
        // explicitly false). When off, the client stops polling this endpoint rather than backing off forever.
        var ollamaConfigured = _configuration.GetValue(OllamaRuntimeGate.RuntimeEnabledConfigurationKey, defaultValue: true);

        try
        {
            var running = await _catalogService.ListRunningOllamaModelsAsync(ct);
            await Send.OkAsync(LocalModelsMapper.ToRunningResponse(running, ollamaConfigured), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // An unreachable Ollama endpoint (HttpRequestException) is expected in desktop mode and this endpoint is polled by the loaded-models page — log it at Debug so it
            // does not flood the console. Any OTHER failure is unexpected and stays at Warning. Mirrors ListLocalModelsEndpoint.
            if (exception is HttpRequestException)
            {
                _logger.LogDebug(exception, "Ollama not reachable while loading the running model list; returning unavailable.");
            }
            else
            {
                _logger.LogWarning(exception, "Running model list could not be loaded.");
            }

            await Send.OkAsync(LocalModelsMapper.ToUnavailableRunningResponse("Local model provider is unavailable.", ollamaConfigured), ct);
        }
    }
}
