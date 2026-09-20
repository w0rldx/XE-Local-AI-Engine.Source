namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     FastEndpoints handler to eject a running llama-server process (POST model-fit/running/eject): thin transport
///     over <see cref="LlamaCppRuntimeOrchestrationService.EjectAsync" />.
/// </summary>
/// <remarks>
///     A graceful eject marks the process evicting (no new inference), waits a bounded window for any in-flight turn to
///     drain, then tears the process down and releases its port; one with no in-flight work goes immediately. Work that
///     does not drain leaves the process <strong>left running</strong> under <c>timed_out_still_busy</c> rather than
///     killing the turn silently, unless <c>force</c> is set, which tears it down anyway and marks the interrupted run
///     operator-ejected. Role vocabulary, idempotence and the 400: <see cref="EjectRunningModelRequest" />.
/// </remarks>
public sealed class EjectRunningModelEndpoint : Endpoint<EjectRunningModelRequest, EjectRunningModelResponse>
{
    private readonly LlamaCppRuntimeOrchestrationService _runtime;

    public EjectRunningModelEndpoint(LlamaCppRuntimeOrchestrationService runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

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
