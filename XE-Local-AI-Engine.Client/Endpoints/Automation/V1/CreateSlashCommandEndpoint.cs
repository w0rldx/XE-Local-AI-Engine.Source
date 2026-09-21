namespace XE_Local_AI_Engine.Client.Endpoints.Automation.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Automation.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Automation;

public sealed class CreateSlashCommandEndpoint : Endpoint<CreateSlashCommandRequest, SlashCommandResponse>
{
    private readonly ISlashCommandService _service;

    public CreateSlashCommandEndpoint(ISlashCommandService service)
    {
        _service = service;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Automation.Commands);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<SlashCommandResponse>(StatusCodes.Status201Created)
                                      .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CreateSlashCommandRequest req, CancellationToken ct)
    {
        try
        {
            var item = await _service.CreateAsync(req.ToInput(), ct);
            await Send.CreatedAtAsync<GetSlashCommandEndpoint>(new
            {
                commandId = item.Id
            }, item.ToResponse(), cancellation: ct);
        }
        catch (SlashCommandConflictException exception)
        {
            await Send.ResultAsync(Results.Problem(statusCode: StatusCodes.Status409Conflict, title: exception.Message));
        }
    }
}
