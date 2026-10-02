namespace XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     The two search knobs <c>KnowledgeSearchService</c> reads through <see cref="INodeRuntimeSettings" />, answered from the
///     options a retrieval test already builds, so each test keeps sizing its adaptive gate and latency budget in one place.
/// </summary>
internal static class KnowledgeSearchRuntimeSettings
{
    public static INodeRuntimeSettings From(IOptions<KnowledgeBaseOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = Substitute.For<INodeRuntimeSettings>();
        settings.GetKnowledgeAdaptiveRerankingEnabledAsync(Arg.Any<CancellationToken>()).Returns(options.Value.AdaptiveRerankingEnabled);
        settings.GetKnowledgeRetrievalLatencyBudgetMsAsync(Arg.Any<CancellationToken>()).Returns(options.Value.RetrievalLatencyBudgetMilliseconds);
        return settings;
    }
}
