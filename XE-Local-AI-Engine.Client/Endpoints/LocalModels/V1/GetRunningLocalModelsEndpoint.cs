namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Lists the models the local runtime currently holds in memory (RAM/VRAM). On provider-unreachable, returns an
///     OK-empty/unavailable response (never a 500) so the loaded-models page can poll and degrade gracefully — mirroring
///     <see cref="ListLocalModelsEndpoint" />. The response also reports whether the Ollama runtime is configured at all
///     (<see cref="RunningLocalModelsResponse.OllamaConfigured" />) so the client stops polling when it is switched off.
/// </summary>
public sealed class GetRunningLocalModelsEndpoint(
    IOllamaModelService modelService,
    IConfiguration configuration,
    ILogger<GetRunningLocalModelsEndpoint> logger) : EndpointWithoutRequest<RunningLocalModelsResponse>
{
    private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    private readonly ILogger<GetRunningLocalModelsEndpoint> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IOllamaModelService _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));

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
            var running = await _modelService.ListRunningModelsAsync(ct).ConfigureAwait(false);
            await Send.OkAsync(LocalModelsMapper.ToRunningResponse(running, ollamaConfigured), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // An unreachable Ollama endpoint (HttpRequestException) is expected in desktop mode and this endpoint is
            // polled by the loaded-models page — log it at Debug so it doesn't flood the console. Any OTHER failure is
            // unexpected and stays at Warning. Mirrors ListLocalModelsEndpoint.
            if (exception is HttpRequestException)
            {
                _logger.LogDebug(exception, "Ollama not reachable while loading the running model list; returning unavailable.");
            }
            else
            {
                _logger.LogWarning(exception, "Running model list could not be loaded.");
            }

            await Send.OkAsync(LocalModelsMapper.ToUnavailableRunningResponse("Local model provider is unavailable.", ollamaConfigured), ct)
                      .ConfigureAwait(false);
        }
    }
}
