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
