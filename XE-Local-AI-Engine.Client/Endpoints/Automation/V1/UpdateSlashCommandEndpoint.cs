namespace XE_Local_AI_Engine.Client.Endpoints.Automation.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Automation.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Automation;

public sealed class UpdateSlashCommandEndpoint : Endpoint<UpdateSlashCommandRequest, SlashCommandResponse>
{
    private readonly ISlashCommandService _service;

    public UpdateSlashCommandEndpoint(ISlashCommandService service)
    {
        _service = service;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.Automation.CommandById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<SlashCommandResponse>(StatusCodes.Status200OK)
                                      .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(UpdateSlashCommandRequest req, CancellationToken ct)
    {
        try
        {
            var item = await _service.UpdateAsync(req.CommandId, req.ToInput(), ct);
            if (item is null)
            {
                await Send.NotFoundAsync(ct);
                return;
            }

            await Send.OkAsync(item.ToResponse(), ct);
        }
        catch (SlashCommandConflictException exception)
        {
            await Send.ResultAsync(Results.Problem(statusCode: StatusCodes.Status409Conflict, title: exception.Message));
        }
    }
}
