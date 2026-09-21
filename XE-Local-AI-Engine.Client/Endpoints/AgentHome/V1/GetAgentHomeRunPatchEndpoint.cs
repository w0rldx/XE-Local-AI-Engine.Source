namespace XE_Local_AI_Engine.Client.Endpoints.AgentHome.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.AgentHome.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     One run's exported <c>changes.patch</c> as text, for reading.
/// </summary>
/// <remarks>
///     A viewer, not a step in the apply flow: it runs no <c>git</c>, resolves no selected folder and grants nothing.
///     Landing a patch remains the preview/apply pair, which re-reads the file itself and binds an approval to the
///     hash it reported — so what this route shows can never become what an apply acts on by being shown.
/// </remarks>
public sealed class GetAgentHomeRunPatchEndpoint : Endpoint<AgentHomeRunByIdRequest, AgentHomeRunTextResponse>
{
    private readonly IAgentHomeRunListService _service;

    public GetAgentHomeRunPatchEndpoint(IAgentHomeRunListService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.AgentHomeRuns.PatchText);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.Produces<AgentHomeRunTextResponse>(StatusCodes.Status200OK)
                                             .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                             .Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(AgentHomeRunByIdRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var text = await _service.ReadPatchAsync(req.RunId, ct);
        if (text is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(text.ToResponse(), ct);
    }
}
