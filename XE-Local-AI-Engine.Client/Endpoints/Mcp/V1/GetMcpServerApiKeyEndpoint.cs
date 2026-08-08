namespace XE_Local_AI_Engine.Client.Endpoints.Mcp.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Mcp.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Mcp;

/// <summary>
///     Returns the inbound-MCP credential's non-secret metadata (prefix, timestamps) plus the endpoint URL to point a
///     client at. It cannot return the key itself — the node keeps only a one-way digest, and the response type has no
///     field for one. Answers 200 with <c>configured=false</c> rather than 404 when no key exists, so the settings page
///     can render the empty state from one call.
/// </summary>
public sealed class GetMcpServerApiKeyEndpoint(IMcpServerApiKeyService apiKeyService)
    : EndpointWithoutRequest<McpServerApiKeyStatusResponse>
{
    private readonly IMcpServerApiKeyService _apiKeyService = apiKeyService ?? throw new ArgumentNullException(nameof(apiKeyService));

    public override void Configure()
    {
        Get(LocalApiRoutes.Mcp.ServerApiKey);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var view = await _apiKeyService.GetAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(McpServerApiKeyMapper.ToStatus(view, HttpContext), ct).ConfigureAwait(false);
    }
}
