namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

public sealed class StartLlamaCppSourceBuildEndpoint : Endpoint<StartLlamaCppSourceBuildRequest, StartLlamaCppSourceBuildResponse>
{
    private readonly LlamaCppRuntimeOrchestrationService _runtime;
    private readonly INodeRuntimeSettings _nodeRuntimeSettings;

    public StartLlamaCppSourceBuildEndpoint(
        LlamaCppRuntimeOrchestrationService runtime,
        INodeRuntimeSettings nodeRuntimeSettings)
    {
        _runtime = runtime;
        _nodeRuntimeSettings = nodeRuntimeSettings;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.SourceBuild);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<StartLlamaCppSourceBuildResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest)
                               .Produces<LlamaCppSourceBuildBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(StartLlamaCppSourceBuildRequest request, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
        {
            await BlockAsync("not-linux", "In-app source builds are available on Linux only.");
            return;
        }

        if (await LlamaCppPrebuiltRuntimeMutationGuard
                  .IsKeepModelWarmEnabledAsync(_nodeRuntimeSettings, ct))
        {
            await BlockAsync("keep-model-warm-enabled", LlamaCppPrebuiltRuntimeMutationGuard.KeepModelWarmBlockedMessage);
            return;
        }

        // The service normalizes the request itself; normalizing here as well would hand StartAsync a request whose
        // server-selected fields are already populated, and the strict official-source rules would reject it.
        try
        {
            var result = await _runtime.StartSourceBuildAsync(request.ToContract(), ct);
            var blocked = LlamaCppSourceBuildStartEndpointSupport.MapBlocked(result.Outcome,
                LlamaCppSourceBuildStartEndpointSupport.SourceBuildKind);
            if (blocked is not null)
            {
                var (reason, message) = blocked.Value;

                // Only the process gate reports a count; every other rejection leaves it null as before.
                await Send.ResultAsync(Results.Conflict(new LlamaCppSourceBuildBlockedResponse
                {
                    Reason = reason,
                    Message = message,
                    RunningProcessCount = result.Outcome == LlamaCppSourceBuildStartOutcome.ProcessesRunning
                        ? result.RunningProcessCount
                        : null
                }));
                return;
            }

            await Send.OkAsync(new StartLlamaCppSourceBuildResponse
            {
                Started = true,
                Status = _runtime.GetSourceBuildStatus().ToResponse()
            }, ct);
        }
        catch (LlamaRuntimeException exception)
        {
            await BlockAsync("prerequisites", exception.Message);
        }
    }

    private Task BlockAsync(string reason, string message)
    {
        return Send.ResultAsync(Results.Conflict(new LlamaCppSourceBuildBlockedResponse
        {
            Reason = reason,
            Message = message
        }));
    }
}
