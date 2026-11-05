namespace XE_Local_AI_Engine.AI.Agent.Tests.Invocation;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The one range table both sampling policies read: <see cref="InvocationSamplingRules.Validate" /> must refuse exactly
///     the values <see cref="InvocationSamplingRules.Apply" /> silently skips, or Direct Inference and chat disagree.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class InvocationSamplingRulesTests
{
    public static IEnumerable<Func<InvocationSamplingOptions>> OutOfRange()
    {
        yield return () => new InvocationSamplingOptions
        {
            Temperature = 2.01f
        };
        yield return () => new InvocationSamplingOptions
        {
            Temperature = float.NaN
        };
        yield return () => new InvocationSamplingOptions
        {
            TopP = -0.1f
        };
        yield return () => new InvocationSamplingOptions
        {
            TopK = 0
        };
        yield return () => new InvocationSamplingOptions
        {
            MinP = 1.5f
        };
        yield return () => new InvocationSamplingOptions
        {
            PresencePenalty = -2.5f
        };
        yield return () => new InvocationSamplingOptions
        {
            FrequencyPenalty = 3f
        };
        yield return () => new InvocationSamplingOptions
        {
            RepeatPenalty = -1f
        };
        yield return () => new InvocationSamplingOptions
        {
            RepeatLastN = -2
        };
        yield return () => new InvocationSamplingOptions
        {
            Seed = -2
        };
        yield return () => new InvocationSamplingOptions
        {
            NumCtx = 0
        };
        yield return () => new InvocationSamplingOptions
        {
            MaxOutputTokens = -5
        };
    }

    [Test]
    [MethodDataSource(nameof(OutOfRange))]
    public void AnOutOfRangeValue_IsRefusedByValidate_AndSkippedByApply(InvocationSamplingOptions sampling)
    {
        var errors = InvocationSamplingRules.Validate(sampling);
        var options = new ChatOptions();
        AdditionalPropertiesDictionary additionalProperties = [];

        InvocationSamplingRules.Apply(options, additionalProperties, sampling);

        AssertEx.Equal(expected: 1, errors.Count, "exactly the one bad field is named");
        AssertEx.Null(options.Temperature);
        AssertEx.Null(options.TopP);
        AssertEx.Null(options.TopK);
        AssertEx.Null(options.PresencePenalty);
        AssertEx.Null(options.FrequencyPenalty);
        AssertEx.Null(options.Seed);
        AssertEx.Null(options.MaxOutputTokens);
        AssertEx.Empty(additionalProperties, "the model default stays for the skipped field");
    }

    [Test]
    public void InRangeBoundaryValues_PassValidate_AndAreAllApplied()
    {
        var sampling = new InvocationSamplingOptions
        {
            Temperature = 2f,
            TopP = 0f,
            TopK = 1,
            MinP = 1f,
            PresencePenalty = -2f,
            FrequencyPenalty = 2f,
            RepeatPenalty = 0f,
            RepeatLastN = -1,
            Seed = -1,
            NumCtx = 512,
            MaxOutputTokens = 1024,
            ReasoningBudgetTokens = 1
        };
        var options = new ChatOptions();
        AdditionalPropertiesDictionary additionalProperties = [];

        var errors = InvocationSamplingRules.Validate(sampling);
        InvocationSamplingRules.Apply(options, additionalProperties, sampling);

        AssertEx.Empty(errors);
        AssertEx.Equal(2f, options.Temperature);
        AssertEx.Equal(0f, options.TopP);
        AssertEx.Equal(1, options.TopK);
        AssertEx.Equal(-1L, options.Seed);
        AssertEx.Equal(512, options.MaxOutputTokens, "the output cap is clamped to the requested window");
        AssertEx.Equal(4, additionalProperties.Count, "num_ctx, min_p, repeat_penalty and repeat_last_n ride the side channel");
    }

    [Test]
    public void Validate_NamesEveryBadField()
    {
        var errors = InvocationSamplingRules.Validate(new InvocationSamplingOptions
        {
            Temperature = -1f,
            TopK = -3,
            ReasoningBudgetTokens = 0
        });

        AssertEx.Equal(3, errors.Count);
        AssertEx.Contains(errors, "Temperature must be between 0 and 2.");
        AssertEx.Contains(errors, "Top K must be greater than 0.");
        AssertEx.Contains(errors, "Reasoning budget must be greater than 0.");
    }
}
