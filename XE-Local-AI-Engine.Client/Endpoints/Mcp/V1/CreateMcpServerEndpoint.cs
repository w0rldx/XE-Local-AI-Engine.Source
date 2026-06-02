namespace XE_Local_AI_Engine.Client.Endpoints.Mcp.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Mcp.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Mcp;

public sealed class CreateMcpServerEndpoint(IMcpServerService mcpServerService)
    : Endpoint<CreateMcpServerRequest, McpServerResponse>
{
    private readonly IMcpServerService _mcpServerService = mcpServerService ?? throw new ArgumentNullException(nameof(mcpServerService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Mcp.Servers);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CreateMcpServerRequest req, CancellationToken ct)
    {
        try
        {
            var record = await _mcpServerService.CreateAsync(req.ToInput(), ct).ConfigureAwait(false);
            await Send.CreatedAtAsync<GetMcpServerEndpoint>(new
                {
                    mcpServerId = record.Id
                },
                record.ToResponse(),
                cancellation: ct).ConfigureAwait(false);
        }
        catch (McpServerValidationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
