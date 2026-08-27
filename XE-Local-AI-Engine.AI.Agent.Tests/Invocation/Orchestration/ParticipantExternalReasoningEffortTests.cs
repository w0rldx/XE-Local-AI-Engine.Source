namespace XE_Local_AI_Engine.AI.Agent.Tests.Invocation.Orchestration;

using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The external-provider effort marker on the orchestration path, which bakes its reasoning into the participant
///     agent at construction and so has to mirror the single-agent factory exactly.
/// </summary>
public sealed class ParticipantExternalReasoningEffortTests
{
    private const string ExternalModel = "ext:local-box/qwen3";

    [Test]
    public void Build_ForAnExternalModelWithAGradedEffort_CarriesTheMarker()
    {
        var properties = ParticipantReasoningOptions.Build("high", supportsThinking: true, modelId: ExternalModel);

        AssertEx.True(properties.TryGetValue<string>(ParticipantReasoningOptions.ExternalReasoningEffortMarkerKey, out var effort));
        AssertEx.Equal("high", effort);
    }

    [Test]
    public void Build_ForANonExternalModel_OmitsTheMarker()
    {
        // Byte-identical no-override guarantee: a model that cannot read the marker must not carry it.
        var properties = ParticipantReasoningOptions.Build("high", supportsThinking: true, modelId: "qwen3-27b.gguf");

        AssertEx.False(properties.ContainsKey(ParticipantReasoningOptions.ExternalReasoningEffortMarkerKey));
    }

    [Test]
    public void Build_ForAnExternalModelWithNoEffort_OmitsTheMarker()
    {
        // Absence is what lets the model's REGISTERED default effort apply.
        var properties = ParticipantReasoningOptions.Build(reasoningEffort: null, supportsThinking: true, modelId: ExternalModel);

        AssertEx.False(properties.ContainsKey(ParticipantReasoningOptions.ExternalReasoningEffortMarkerKey));
    }

    [Test]
    public void Build_ForANonThinkingExternalModel_OmitsTheMarker()
    {
        // The marker rides the graded branch only, matching the capability gating every other reasoning field uses.
        var properties = ParticipantReasoningOptions.Build("high", supportsThinking: false, modelId: ExternalModel);

        AssertEx.False(properties.ContainsKey(ParticipantReasoningOptions.ExternalReasoningEffortMarkerKey));
    }
}
