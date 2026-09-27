namespace XE_Local_AI_Engine.Client.Endpoints.Python.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Python.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ManagedPython;

/// <summary>The shared uv toolchain and every feature environment's state. Read-only: it never provisions.</summary>
public sealed class GetManagedPythonStatusEndpoint : EndpointWithoutRequest<ManagedPythonStatusResponse>
{
    private readonly ManagedPythonStatusService _status;

    public GetManagedPythonStatusEndpoint(ManagedPythonStatusService status)
    {
        _status = status;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Python.Status);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<ManagedPythonStatusResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Send.OkAsync((await _status.GetStatusAsync(ct)).ToResponse(), ct);
    }
}
