namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>Builds the provider-boundary budget for one run: the node options with the live node-settings knobs read once.</summary>
/// <remarks>
///     Shared by <see cref="InvocationRunner" /> (chat turns) and the inbound MCP run path so both enforce the same ceilings.
///     A package's exact output reservation, when it has one, replaces the node floor.
/// </remarks>
internal static class ProviderCallBudgetOptionsResolver
{
    public static async Task<ProviderCallBudgetOptions> ResolveAsync(ProviderCallBudgetOptions nodeOptions,
        INodeRuntimeSettings runtimeSettings,
        int defaultContextTokens,
        int? reservedOutputTokensOverride,
        CancellationToken cancellationToken)
    {
        return new ProviderCallBudgetOptions
        {
            MaxProviderCallsPerInvocation = nodeOptions.MaxProviderCallsPerInvocation,
            MaxCumulativeInputTokens = await runtimeSettings.GetProviderBudgetMaxCumulativeInputTokensAsync(cancellationToken),
            DefaultContextTokens = defaultContextTokens,
            ReservedOutputTokenFloor = reservedOutputTokensOverride is { } reserved && reserved >= 0
                ? reserved
                : nodeOptions.ReservedOutputTokenFloor,
            RecentMessagesToKeep = await runtimeSettings.GetProviderBudgetRecentMessagesToKeepAsync(cancellationToken),
            OversizedToolResultExcerptChars = nodeOptions.OversizedToolResultExcerptChars
        };
    }
}
