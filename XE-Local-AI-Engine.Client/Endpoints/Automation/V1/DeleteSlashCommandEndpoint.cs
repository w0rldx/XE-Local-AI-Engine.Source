namespace XE_Local_AI_Engine.Client.Endpoints.Automation.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Automation;

public sealed class DeleteSlashCommandEndpoint : Endpoint<SlashCommandByIdRequest>
{
    private readonly ISlashCommandService _service;

    public DeleteSlashCommandEndpoint(ISlashCommandService service)
    {
        _service = service;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Automation.CommandById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(SlashCommandByIdRequest req, CancellationToken ct)
    {
        if (!await _service.DeleteAsync(req.CommandId, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
