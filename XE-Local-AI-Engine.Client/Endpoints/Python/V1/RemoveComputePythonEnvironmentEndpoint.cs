namespace XE_Local_AI_Engine.Client.Endpoints.Python.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Python.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ManagedPython;

/// <summary>Deletes the Compute environment and its state record; the next <c>run_python</c> call provisions it again. The shared toolchain store is never touched.</summary>
/// <remarks>
///     Every refusal is a typed 409 value (ADR 0009), not the exception envelope: <c>unsupported</c>, <c>busy</c> (a provision
///     or a <c>run_python</c> call holds the runtime) and <c>failed</c>, which an I/O error during the delete surfaces as,
///     with a user-safe message.
/// </remarks>
public sealed class RemoveComputePythonEnvironmentEndpoint : EndpointWithoutRequest<ManagedPythonStatusResponse>
{
    private readonly ManagedPythonStatusService _status;

    public RemoveComputePythonEnvironmentEndpoint(ManagedPythonStatusService status)
    {
        _status = status;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Python.ComputeRemove);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<ManagedPythonStatusResponse>(StatusCodes.Status200OK)
                               .Produces<ManagedPythonBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (ManagedPythonBlockedEndpointSupport.BlockedOrNull(await _status.RemoveComputeAsync(ct)) is { } blocked)
        {
            await Send.ResultAsync(blocked);
            return;
        }

        await Send.OkAsync((await _status.GetStatusAsync(ct)).ToResponse(), ct);
    }
}
