namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     FastEndpoints handler to eject a running llama-server process (POST model-fit/running/eject). Thin transport over
///     <see cref="LlamaCppRuntimeOrchestrationService.EjectAsync" />: by default the eject is GRACEFUL — it marks the process
///     evicting (no new inference), waits a bounded window for any in-flight turn to drain, then tears the process down
///     and releases its port. A process with no in-flight work is torn down immediately. When in-flight work does not
///     drain within the window and <c>force</c> was not set, the process is <strong>left running</strong> and the
///     response <see cref="EjectRunningModelResponse.Outcome" /> reports <c>timed_out_still_busy</c> rather than killing
///     the running turn silently; setting <c>force</c> tears it down anyway and marks the interrupted run
///     operator-ejected. Eject is idempotent (a not-running process reports <c>not_running</c>). Role is
///     <c>chat|embedding</c> (defaulting to chat); an unknown role is rejected with a 400.
/// </summary>
public sealed class EjectRunningModelEndpoint(LlamaCppRuntimeOrchestrationService runtime)
    : Endpoint<EjectRunningModelRequest, EjectRunningModelResponse>
{
    private readonly LlamaCppRuntimeOrchestrationService _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.RunningEject);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(EjectRunningModelRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ModelName))
        {
            AddError("A model name is required.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        if (ModelFitMapper.TryParseRole(req.Role) is not { } role)
        {
            AddError("Role is not supported.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var modelName = req.ModelName.Trim();
        NodeMetrics.ModelEjectTotal.Add(1, new KeyValuePair<string, object?>("outcome", "requested"));

        var outcome = await _runtime.EjectAsync(modelName, role, req.Force, ct);
        NodeMetrics.ModelEjectTotal.Add(1, new KeyValuePair<string, object?>("outcome", ToWireOutcome(outcome)));

        await Send.OkAsync(new EjectRunningModelResponse
            {
                ModelName = modelName,
                Role = role.ToWireString(),
                Outcome = ToWireOutcome(outcome)
            },
            ct);
    }

    private static string ToWireOutcome(LlamaServerEjectOutcome outcome)
    {
        return outcome switch
        {
            LlamaServerEjectOutcome.Ejected => "ejected",
            LlamaServerEjectOutcome.TimedOutStillBusy => "timed_out_still_busy",
            LlamaServerEjectOutcome.ForcedWhileBusy => "forced",
            _ => "not_running"
        };
    }
}
