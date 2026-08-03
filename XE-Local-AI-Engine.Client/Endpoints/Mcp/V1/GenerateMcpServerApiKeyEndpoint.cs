namespace XE_Local_AI_Engine.Client.Endpoints.Mcp.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Mcp.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Mcp;

/// <summary>
///     Mints a new inbound-MCP credential, REPLACING any existing one. This is both "generate" and "rotate": there is
///     one key, so a regenerate immediately invalidates the previous value and every client configured with it.
/// </summary>
public sealed class GenerateMcpServerApiKeyEndpoint(IMcpServerApiKeyService apiKeyService)
    : EndpointWithoutRequest<McpServerApiKeyStatusResponse>
{
    private readonly IMcpServerApiKeyService _apiKeyService = apiKeyService ?? throw new ArgumentNullException(nameof(apiKeyService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Mcp.ServerApiKey);
        Policies(NodeAuthorizationPolicies.Operator);
        // Route-only POST with no body and therefore no Content-Type; FastEndpoints' default Accepts metadata would
        // answer that with 415. Same override the scheduler's body-less actions use.
        Description(x => x.Accepts<EmptyRequest>());
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var view = await _apiKeyService.GenerateAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(McpServerApiKeyMapper.ToStatus(view, HttpContext), ct).ConfigureAwait(false);
    }
}
