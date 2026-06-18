namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     FastEndpoints handler to evict a running llama-server process (POST model-fit/running/eject). Thin transport over
///     the Lane A <see cref="ILlamaServerProcessSupervisor.EvictAsync" />: it tree-kills the <c>(model, role)</c> process
///     and releases its port. Eviction is idempotent (a not-running process is a no-op). The role is <c>chat|embedding</c>
///     (defaulting to chat); an unknown role is rejected with a 400.
/// </summary>
public sealed class EjectRunningModelEndpoint(ILlamaServerProcessSupervisor supervisor)
    : Endpoint<EjectRunningModelRequest, EjectRunningModelResponse>
{
    private readonly ILlamaServerProcessSupervisor _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));

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
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        if (ModelFitMapper.TryParseRole(req.Role) is not { } role)
        {
            AddError("Role is not supported.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var modelName = req.ModelName.Trim();
        await _supervisor.EvictAsync(modelName, role, ct).ConfigureAwait(false);

        await Send.OkAsync(new EjectRunningModelResponse
            {
                ModelName = modelName,
                Role = role.ToWireString()
            },
            ct).ConfigureAwait(false);
    }
}
