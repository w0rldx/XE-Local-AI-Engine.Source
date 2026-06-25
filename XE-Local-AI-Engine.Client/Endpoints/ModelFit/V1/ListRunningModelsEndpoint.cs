namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     FastEndpoints handler for the running llama-server processes (GET model-fit/running). There is no dedicated
///     list-running seam — the running models are derived from the llama-server process supervisor's
///     <see cref="ILlamaServerProcessSupervisor.CheckHealthAsync" /> snapshot (one row per running <c>(model, role)</c>
///     process). On a supervisor failure it returns an OK-empty list (never a 500) so the running panel can poll and
///     degrade. Each row's diagnostics are already sanitized (no internal paths/secrets).
/// </summary>
public sealed class ListRunningModelsEndpoint(
    ILlamaServerProcessSupervisor supervisor,
    ILogger<ListRunningModelsEndpoint> logger) : EndpointWithoutRequest<ListRunningModelsResponse>
{
    private readonly ILogger<ListRunningModelsEndpoint> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ILlamaServerProcessSupervisor _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.Running);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        try
        {
            var health = await _supervisor.CheckHealthAsync(ct).ConfigureAwait(false);
            await Send.OkAsync(new ListRunningModelsResponse
                {
                    Items = [.. health.Select(static process => process.ToResponse())]
                },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Running llama-server process list could not be loaded.");
            await Send.OkAsync(new ListRunningModelsResponse
                {
                    Items = []
                },
                ct).ConfigureAwait(false);
        }
    }
}
