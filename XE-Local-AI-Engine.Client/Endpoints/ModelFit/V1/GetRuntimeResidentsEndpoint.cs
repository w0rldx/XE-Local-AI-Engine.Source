namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

/// <summary>
///     FastEndpoints handler for the image and whisper daemons held in memory (GET model-fit/runtime-residents), for
///     the top-bar widget next to the llama.cpp rows of <c>GET model-fit/running</c>.
/// </summary>
/// <remarks>
///     Served from <see cref="RuntimeResidentsService" />, in-memory state only, so it is cheap enough to poll on every
///     page. A node with transcription switched off answers 200 without whisper rows.
/// </remarks>
public sealed class GetRuntimeResidentsEndpoint : EndpointWithoutRequest<RuntimeResidentsResponse>
{
    private readonly RuntimeResidentsService _residents;

    public GetRuntimeResidentsEndpoint(RuntimeResidentsService residents)
    {
        ArgumentNullException.ThrowIfNull(residents);
        _residents = residents;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.RuntimeResidents);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override Task HandleAsync(CancellationToken ct)
    {
        return Send.OkAsync(_residents.GetResidents().ToResponse(), ct);
    }
}
