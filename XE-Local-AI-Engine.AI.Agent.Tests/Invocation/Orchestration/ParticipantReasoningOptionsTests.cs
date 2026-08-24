namespace XE_Local_AI_Engine.AI.Agent.Tests.Invocation.Orchestration;

using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The orchestration/spawn path bakes its reasoning into the participant agent at construction (it never receives
///     per-run <c>RunOptions</c>), so the thinking budget has to be mirrored here exactly as the single-agent factory
///     sets it — otherwise a workflow participant is the one caller that still free-runs its reasoning until the context
///     window is exhausted.
/// </summary>
public sealed class ParticipantReasoningOptionsTests
{
    [Test]
    [Arguments("low", 2048)]
    [Arguments("medium", 8192)]
    [Arguments("high", 24576)]
    public void Build_ThinkingCapableWithGradedEffort_CarriesTheMappedReasoningBudget(string effort, int expectedBudget)
    {
        var properties = ParticipantReasoningOptions.Build(effort, supportsThinking: true);

        AssertEx.True(properties.TryGetValue<int>(ParticipantReasoningOptions.LlamaReasoningBudgetMarkerKey, out var budget));
        AssertEx.Equal(expectedBudget, budget);
    }

    [Test]
    [Arguments(null, true)]
    [Arguments("none", true)]
    [Arguments("high", false)]
    public void Build_WithoutGradedEffortOrThinkingCapability_OmitsTheReasoningBudget(string? effort, bool supportsThinking)
    {
        var properties = ParticipantReasoningOptions.Build(effort, supportsThinking);

        AssertEx.False(properties.ContainsKey(ParticipantReasoningOptions.LlamaReasoningBudgetMarkerKey));
    }
}
