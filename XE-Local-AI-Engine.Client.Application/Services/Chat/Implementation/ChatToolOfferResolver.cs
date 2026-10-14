namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Resolves whether a send offers tools and, if so, which ones travel in the runtime package.
/// </summary>
/// <remarks>
///     Shared by send, regenerate, the context estimate and the Default Assistant offer read. Node EnableTools and model
///     tool support gate every offer; the local-tools toggle gates only the node-wide one, never an agent's own non-empty
///     list. The unbound fallback applies the same tighten-only approval policy as a bound definition. See
///     docs/wiki/05-chat.md, "MCP &amp; local tools offered during a chat run".
/// </remarks>
internal static class ChatToolOfferResolver
{
    public static async Task<ChatToolOffer> ResolveAsync(bool useLocalTools,
        ChatTurnResolution resolution,
        INodeRuntimeSettings runtimeSettings,
        ILocalToolOfferProvider localToolOfferProvider,
        IToolApprovalPolicy toolApprovalPolicy,
        CancellationToken cancellationToken)
    {
        var enableTools = await runtimeSettings.GetEnableToolsAsync(cancellationToken);
        var toolsRequested = useLocalTools || resolution.Resolved is { DeclaresToolList: true };
        if (!(toolsRequested && enableTools && resolution.SupportsTools))
        {
            return new ChatToolOffer
            {
                OfferTools = false,
                AllowedTools = null,
                WithheldForCapability = toolsRequested && enableTools && !resolution.SupportsTools
            };
        }

        if (resolution.Resolved?.AllowedTools is { } resolvedAllowedTools)
        {
            return new ChatToolOffer
            {
                OfferTools = true,
                AllowedTools = resolvedAllowedTools,
                TrimmedByAllowList = resolution.IsOfferTrimmedByAllowList(localToolOfferProvider)
            };
        }

        var fallbackOffer = await localToolOfferProvider.GetOfferedToolsAsync(resolution.ActiveModel, resolution.EffectiveModelIsCloud, resolution.EffectiveModelCloudGrants, cancellationToken);
        return new ChatToolOffer
        {
            OfferTools = true,
            AllowedTools =
            [
                .. fallbackOffer.Select(tool => tool with
                {
                    RequiresApproval = toolApprovalPolicy.RequiresApproval(tool.Name, tool.Category, tool.RequiresApproval)
                })
            ],
            TrimmedByAllowList = resolution.IsOfferTrimmedByAllowList(localToolOfferProvider)
        };
    }
}
