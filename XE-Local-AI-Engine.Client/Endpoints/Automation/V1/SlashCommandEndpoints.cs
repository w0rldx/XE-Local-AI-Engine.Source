namespace XE_Local_AI_Engine.Client.Endpoints.Automation.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Automation.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Automation;

public sealed class ListSlashCommandsEndpoint(ISlashCommandService service) : EndpointWithoutRequest<ListSlashCommandsResponse>
{
    public override void Configure()
    {
        Get(LocalApiRoutes.Automation.Commands);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<ListSlashCommandsResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(CancellationToken ct) =>
        await Send.OkAsync(new ListSlashCommandsResponse
        {
            Items = [.. (await service.ListAsync(ct).ConfigureAwait(false)).Select(item => item.ToResponse())]
        }, ct).ConfigureAwait(false);
}

public sealed class GetSlashCommandEndpoint(ISlashCommandService service) : Endpoint<SlashCommandByIdRequest, SlashCommandResponse>
{
    public override void Configure()
    {
        Get(LocalApiRoutes.Automation.CommandById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<SlashCommandResponse>(StatusCodes.Status200OK).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(SlashCommandByIdRequest req, CancellationToken ct)
    {
        var item = await service.GetByIdAsync(req.CommandId, ct).ConfigureAwait(false);
        if (item is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(item.ToResponse(), ct).ConfigureAwait(false);
    }
}

public sealed class CreateSlashCommandEndpoint(ISlashCommandService service) : Endpoint<CreateSlashCommandRequest, SlashCommandResponse>
{
    public override void Configure()
    {
        Post(LocalApiRoutes.Automation.Commands);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<SlashCommandResponse>(StatusCodes.Status201Created)
                                      .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CreateSlashCommandRequest req, CancellationToken ct)
    {
        try
        {
            var item = await service.CreateAsync(req.ToInput(), ct).ConfigureAwait(false);
            await Send.CreatedAtAsync<GetSlashCommandEndpoint>(new
            {
                commandId = item.Id
            }, item.ToResponse(), cancellation: ct).ConfigureAwait(false);
        }
        catch (SlashCommandValidationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
        catch (SlashCommandConflictException exception)
        {
            await Send.ResultAsync(Results.Problem(statusCode: StatusCodes.Status409Conflict, title: exception.Message)).ConfigureAwait(false);
        }
    }
}

public sealed class UpdateSlashCommandEndpoint(ISlashCommandService service) : Endpoint<UpdateSlashCommandRequest, SlashCommandResponse>
{
    public override void Configure()
    {
        Put(LocalApiRoutes.Automation.CommandById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<SlashCommandResponse>(StatusCodes.Status200OK)
                                      .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(UpdateSlashCommandRequest req, CancellationToken ct)
    {
        try
        {
            var item = await service.UpdateAsync(req.CommandId, req.ToInput(), ct).ConfigureAwait(false);
            if (item is null)
            {
                await Send.NotFoundAsync(ct).ConfigureAwait(false);
                return;
            }

            await Send.OkAsync(item.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (SlashCommandValidationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
        catch (SlashCommandConflictException exception)
        {
            await Send.ResultAsync(Results.Problem(statusCode: StatusCodes.Status409Conflict, title: exception.Message)).ConfigureAwait(false);
        }
    }
}

public sealed class DeleteSlashCommandEndpoint(ISlashCommandService service) : Endpoint<SlashCommandByIdRequest>
{
    public override void Configure()
    {
        Delete(LocalApiRoutes.Automation.CommandById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(SlashCommandByIdRequest req, CancellationToken ct)
    {
        if (!await service.DeleteAsync(req.CommandId, ct).ConfigureAwait(false))
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}
