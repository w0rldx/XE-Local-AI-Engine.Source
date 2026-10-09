namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Resolves whether a send offers tools and, if so, which ones travel in the runtime package.
/// </summary>
/// <remarks>
///     Shared by the send path and the Default Assistant offer read, so the agent form shows the list a turn really
///     hands the model. Tools are offered only when the client asked, the node tool engine is enabled and the model
///     advertises the capability. A bound definition narrows the offer to its allowed set (node approval policy already
///     applied, custom tools merged, in <see cref="ChatTurnResolver" />); the unbound fallback builds the raw offer here
///     and applies the SAME tighten-only policy, or an unbound turn would bypass a node-wide one.
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
        if (!(useLocalTools && enableTools && resolution.SupportsTools))
        {
            return new ChatToolOffer
            {
                OfferTools = false,
                AllowedTools = null,
                WithheldForCapability = useLocalTools && enableTools && !resolution.SupportsTools
            };
        }

        if (resolution.Resolved?.AllowedTools is { } resolvedAllowedTools)
        {
            return new ChatToolOffer
            {
                OfferTools = true,
                AllowedTools = resolvedAllowedTools
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
            ]
        };
    }
}
