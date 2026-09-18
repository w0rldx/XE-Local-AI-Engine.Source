namespace XE_Local_AI_Engine.Client.Endpoints.Automation.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Automation.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Automation;

public sealed class ListSlashCommandsEndpoint : EndpointWithoutRequest<ListSlashCommandsResponse>
{
    private readonly ISlashCommandService _service;

    public ListSlashCommandsEndpoint(ISlashCommandService service)
    {
        _service = service;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Automation.Commands);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<ListSlashCommandsResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(CancellationToken ct) =>
        await Send.OkAsync(new ListSlashCommandsResponse
        {
            Items = [.. (await _service.ListAsync(ct)).Select(item => item.ToResponse())]
        }, ct);
}

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
