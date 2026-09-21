namespace XE_Local_AI_Engine.Client.Endpoints.Automation.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Automation.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Automation;

public sealed class GetSlashCommandEndpoint : Endpoint<SlashCommandByIdRequest, SlashCommandResponse>
{
    private readonly ISlashCommandService _service;

    public GetSlashCommandEndpoint(ISlashCommandService service)
    {
        _service = service;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Automation.CommandById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<SlashCommandResponse>(StatusCodes.Status200OK).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(SlashCommandByIdRequest req, CancellationToken ct)
    {
        var item = await _service.GetByIdAsync(req.CommandId, ct);
        if (item is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(item.ToResponse(), ct);
    }
}
