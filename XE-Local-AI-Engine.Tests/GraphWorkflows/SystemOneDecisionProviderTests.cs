namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Decisions;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The provider's own arithmetic and failure classes: the convenience labels at their edges, and which client
///     outcomes a run may retry.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class SystemOneDecisionProviderTests
{
    private static readonly string[] Levels = ["low", "mid", "high"];

    /// <summary>Ties round away from zero by design, so 0.5 is the second level; out-of-range scores clamp to the ends.</summary>
    [Test]
    [Arguments(0.0, "low")]
    [Arguments(0.49, "low")]
    [Arguments(0.5, "mid")]
    [Arguments(1.5, "high")]
    [Arguments(-1.0, "low")]
    [Arguments(99.0, "high")]
    public void NearestLevel_RoundsTiesAwayFromZeroAndClamps(double score, string expected) =>
        AssertEx.Equal(expected, SystemOneDecisionProvider.NearestLevel(score, Levels));

    [Test]
    public void NearestLevel_HasNoLevelForAMissingOrNonFiniteScore()
    {
        AssertEx.Null(SystemOneDecisionProvider.NearestLevel(double.NaN, Levels));
        AssertEx.Null(SystemOneDecisionProvider.NearestLevel(double.PositiveInfinity, Levels));
        AssertEx.Null(SystemOneDecisionProvider.NearestLevel(null, Levels));
    }

    [Test]
    [Arguments(0.5, "yes")]
    [Arguments(0.4999, "no")]
    [Arguments(1.0, "yes")]
    [Arguments(0.0, "no")]
    public void YesOrNo_SaysYesFromExactlyOneHalf(double noul, string expected) =>
        AssertEx.Equal(expected, SystemOneDecisionProvider.YesOrNo(noul));

    [Test]
    public void YesOrNo_HasNoAnswerForAMissingOrNonFiniteLikelihood()
    {
        AssertEx.Null(SystemOneDecisionProvider.YesOrNo(double.NaN));
        AssertEx.Null(SystemOneDecisionProvider.YesOrNo(null));
    }

    /// <summary>Only a failure a second attempt could change is retryable; a refused request or a wrong model is not.</summary>
    [Test]
    [Arguments(LlamaServerSystemOneOutcome.Timeout, GraphWorkflowFailureClass.Timeout)]
    [Arguments(LlamaServerSystemOneOutcome.InvalidRequest, GraphWorkflowFailureClass.ValidationFailed)]
    [Arguments(LlamaServerSystemOneOutcome.NotDecisionModel, GraphWorkflowFailureClass.ValidationFailed)]
    [Arguments(LlamaServerSystemOneOutcome.Transport, GraphWorkflowFailureClass.NodeFailed)]
    [Arguments(LlamaServerSystemOneOutcome.Busy, GraphWorkflowFailureClass.NodeFailed)]
    public async Task AFailedCall_MapsToItsFailureClass(LlamaServerSystemOneOutcome outcome, GraphWorkflowFailureClass expected)
    {
        const string question = "provider-failure-class";
        var client = new FakeSystemOneClient();
        client.Script(question, new LlamaServerSystemOneResult
        {
            Outcome = outcome,
            Detail = "scripted"
        });

        var result = await new SystemOneDecisionProvider(client).DecideAsync(new NativeDecisionRequest
        {
            ModelName = "m",
            State = "s",
            Config = new GraphWorkflowDecisionModelConfig
            {
                Question = question,
                Labels = ["a", "b"],
                Provider = SystemOneDecisionProvider.ProviderName,
                Model = "m",
                InputBindings = new Dictionary<string, string>(StringComparer.Ordinal)
            }
        }, CancellationToken.None);

        AssertEx.Equal(expected, result.FailureClass);
        AssertEx.NotNull(result.FailureReason);
    }
}
