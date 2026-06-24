namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Exposes the node's tool-capable model ids (the migrated <c>AgentHome:ToolCapableModels</c> allowlist, read via
///     <see cref="INodeRuntimeSettings" /> with the stored &gt; seed &gt; default precedence). The agent management UI
///     is the only consumer: it warns when a definition pins a model that is not tool-capable, so tool selection can be
///     disabled rather than silently no-op at runtime.
/// </summary>
public sealed class GetToolCapableModelsEndpoint(INodeRuntimeSettings runtimeSettings)
    : EndpointWithoutRequest<ToolCapableModelsResponse>
{
    private readonly INodeRuntimeSettings _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));

    public override void Configure()
    {
        Get(LocalApiRoutes.Agents.ToolCapableModels);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // Configuration list binding appends rather than replaces, so an env/appsettings entry that repeats a default
        // model id (the default is ["qwen3:8b"]) yields a duplicate in the bound list. Distinct it at the source so the
        // response is a clean set; the offer provider already dedupes via an Ordinal HashSet on the same setting.
        var toolCapableModels = await _runtimeSettings.GetToolCapableModelsAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ToolCapableModelsResponse
            {
                Models = [.. toolCapableModels.Distinct(StringComparer.Ordinal)]
            },
            ct).ConfigureAwait(false);
    }
}
