namespace XE_Local_AI_Engine.Client.Endpoints.Mcp.V1;

using System.Text.Json;
using FastEndpoints;
using Microsoft.AspNetCore.Http.Features;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Mcp.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Mcp;

/// <summary>
///     Mints a new inbound-MCP credential, REPLACING any existing one. This is both "generate" and "rotate": there is
///     one key, so a regenerate immediately invalidates the previous value and every client configured with it.
///     <para>
///         This response is the ONLY place the plaintext key ever appears — the node persists only its SHA-256 digest.
///         A caller that discards this body cannot get the key back from any other endpoint.
///     </para>
/// </summary>
public sealed class GenerateMcpServerApiKeyEndpoint(IMcpServerApiKeyService apiKeyService)
    : EndpointWithoutRequest<GeneratedMcpServerApiKeyResponse>
{
    private readonly IMcpServerApiKeyService _apiKeyService = apiKeyService ?? throw new ArgumentNullException(nameof(apiKeyService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Mcp.ServerApiKey);
        Policies(NodeAuthorizationPolicies.Operator);
        // Runtime must accept the historical POST with no body or Content-Type. OpenAPI's optional typed body is
        // restored by McpServerApiKeyOpenApiOperationProcessor because FastEndpoints exposes only one accepts shape.
        Description(description => description.Accepts<EmptyRequest>());
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var scope = McpServerApiKeyScope.Delegate;
        var request = HttpContext.Request;
        var canHaveBody = HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody
                          ?? request.ContentLength is > 0;
        if (canHaveBody)
        {
            if (!request.HasJsonContentType())
            {
                AddError("The request body must use the application/json media type.");
                await Send.ErrorsAsync(StatusCodes.Status415UnsupportedMediaType, cancellation: ct).ConfigureAwait(false);
                return;
            }

            try
            {
                var body = await request.ReadFromJsonAsync<GenerateMcpServerApiKeyRequest>(ct).ConfigureAwait(false);
                if (body is null)
                {
                    AddError("The request body must contain a valid MCP API key scope.");
                    await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
                    return;
                }

                scope = body.Scope;
            }
            catch (JsonException)
            {
                AddError("Scope must be exactly 'delegate' or 'agentic'.");
                await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
                return;
            }
        }

        var generated = await _apiKeyService.GenerateAsync(scope, ct).ConfigureAwait(false);
        await Send.OkAsync(McpServerApiKeyMapper.ToGenerated(generated, HttpContext), ct).ConfigureAwait(false);
    }
}
