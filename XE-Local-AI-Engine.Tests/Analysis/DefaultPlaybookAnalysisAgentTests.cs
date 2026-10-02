namespace XE_Local_AI_Engine.Tests.Analysis;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Analysis;
using XE_Local_AI_Engine.Client.Services.Analysis.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Insights;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     The analysis agent's node-locality gate: feedback comments never reach a model that is not node-local.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class DefaultPlaybookAnalysisAgentTests
{
    [Test]
    public async Task ProposeAsync_WhenModelIsNotNodeLocal_SkipsWithoutResolvingAProvider()
    {
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        var trust = Substitute.For<IModelTrustResolver>();
        trust.ResolveAsync("ext:cloud-box/gpt", Arg.Any<CancellationToken>()).Returns(ModelTrustLocality.Cloud);
        var agent = new DefaultPlaybookAnalysisAgent(resolver,
            Options.Create(new PlaybookAnalysisOptions()),
            StubNodeRuntimeSettings.Create().WithPlaybookAnalysisModelName("ext:cloud-box/gpt").Build(),
            trust,
            NullLogger<DefaultPlaybookAnalysisAgent>.Instance);

        var proposals = await agent.ProposeAsync(new FeedbackInsightsResult
        {
            AgentDefinitionId = Guid.NewGuid(),
            AgentName = "Agent",
            GeneratedAtUtc = 1_000,
            MinOccurrenceThreshold = 3,
            Overall = new OverallFeedback
            {
                Total = 5,
                Up = 1,
                Down = 4,
                DownRate = 0.8d,
                MeetsThreshold = true
            },
            ByTool = [],
            Exemplars = []
        });

        AssertEx.Empty(proposals);
        await resolver.DidNotReceive().ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
