namespace XE_Local_AI_Engine.Tests.ExternalProviders;

using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Providers.OpenAICompat;
using XE_Local_AI_Engine.Providers.OpenAICompat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The in-process side channel that carries a turn's selected reasoning effort to an external model, and the two
///     spellings of its key that have to agree.
/// </summary>
/// <remarks>
///     The effort is resolved far upstream, in the agent factory, which cannot reference a provider project — the same
///     constraint that made the llama.cpp reasoning budget travel as a marker. So the key literal exists twice, and
///     this suite is what keeps a rename in one of them from silently disabling effort selection in the other.
/// </remarks>
public sealed class ExternalReasoningEffortMarkerTests
{
    private const string ExternalModel = "ext:local-box/qwen3";

    [Test]
    public void TheMarkerKeyIsSpelledIdenticallyOnBothSides()
    {
        // A mismatch would not fail anything loudly: the provider would simply never find a selected effort and would
        // apply the registered default to every turn.
        AssertEx.Equal(ExternalProviderConstants.ReasoningEffortMarkerKey, ReasoningOptionsResolver.ExternalReasoningEffortMarkerKey);
    }

    [Test]
    [Arguments("low", "low")]
    [Arguments("MEDIUM", "medium")]
    [Arguments("  High  ", "high")]
    [Arguments("minimal", "minimal")]
    [Arguments("xhigh", "xhigh")]
    public void ResolveExternalReasoningEffort_CanonicalizesARecognizedEffort(string input, string expected)
    {
        AssertEx.Equal(expected, ReasoningOptionsResolver.ResolveExternalReasoningEffort(ExternalModel, input));
    }

    [Test]
    [Arguments("none")]
    [Arguments("on")]
    public void ResolveExternalReasoningEffort_CarriesTheTurnLevelDecisionsToo(string input)
    {
        // The provider sends no field for either, but the marker's PRESENCE is what stops the registered default from
        // overriding a turn that asked for "off" or for the model's own built-in reasoning.
        AssertEx.Equal(input, ReasoningOptionsResolver.ResolveExternalReasoningEffort(ExternalModel, input));
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("extreme")]
    public void ResolveExternalReasoningEffort_OmitsTheMarkerForABlankOrUnknownEffort(string input)
    {
        // Absence is meaningful: it is what lets the model's registered default effort apply.
        AssertEx.Null(ReasoningOptionsResolver.ResolveExternalReasoningEffort(ExternalModel, input));
    }

    [Test]
    public void ResolveExternalReasoningEffort_OmitsTheMarkerForANonExternalModel()
    {
        // Byte-identical no-override guarantee: a model that cannot read the marker must not carry it.
        AssertEx.Null(ReasoningOptionsResolver.ResolveExternalReasoningEffort("qwen3-27b.gguf", "high"));
        AssertEx.Null(ReasoningOptionsResolver.ResolveExternalReasoningEffort(modelId: null, "high"));
    }

    [Test]
    public void TheProviderClampsWhatTheResolverEmits()
    {
        // The two halves of the contract meet here: the resolver emits the node's seven-value vocabulary, and the
        // provider narrows it to the low|medium|high set that is interoperable across OpenAI, vLLM, llama.cpp and Groq.
        AssertEx.Equal(Microsoft.Extensions.AI.ReasoningEffort.Low,
            ExternalReasoningEffort.ToWireEffort(ReasoningOptionsResolver.ResolveExternalReasoningEffort(ExternalModel, "minimal")));
        AssertEx.Equal(Microsoft.Extensions.AI.ReasoningEffort.High,
            ExternalReasoningEffort.ToWireEffort(ReasoningOptionsResolver.ResolveExternalReasoningEffort(ExternalModel, "xhigh")));
        AssertEx.Null(ExternalReasoningEffort.ToWireEffort(ReasoningOptionsResolver.ResolveExternalReasoningEffort(ExternalModel, "none")));
        AssertEx.Null(ExternalReasoningEffort.ToWireEffort(ReasoningOptionsResolver.ResolveExternalReasoningEffort(ExternalModel, "on")));
    }
}
