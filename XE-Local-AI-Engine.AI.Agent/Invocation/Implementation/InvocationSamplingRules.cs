namespace XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>The accepted range of every per-send sampling override, and the one place that applies them to a request.</summary>
/// <remarks>
///     Two callers read the same ranges with opposite policies: a chat turn <see cref="Apply" />s and silently keeps the
///     model default for an out-of-range value, while Direct Inference <see cref="Validate" />s first and refuses it.
/// </remarks>
internal static class InvocationSamplingRules
{
    /// <summary>
    ///     Applies the developer-gated per-send sampling overrides onto the turn's <see cref="ChatOptions" />.
    /// </summary>
    /// <remarks>
    ///     Native knobs ride the strongly-typed properties; the four without one travel as
    ///     <see cref="AdditionalPropertiesDictionary" /> entries keyed by <see cref="SamplingOptionKeys" />, and reach
    ///     BOTH runtimes. Each field is applied only when set and only when it passes a defensive range guard, so
    ///     NaN, negative or out-of-range keeps the model default. See docs/wiki/04-agent-mode.md ("Per-send sampling
    ///     reaches two runtimes").
    /// </remarks>
    public static void Apply(ChatOptions chatOptions,
        AdditionalPropertiesDictionary additionalProperties,
        InvocationSamplingOptions? sampling)
    {
        if (sampling is null)
        {
            return;
        }

        // Temperature is accepted in [0, 2] (the UI cap); out-of-range → skip and keep the model default.
        if (IsValidTemperature(sampling.Temperature))
        {
            chatOptions.Temperature = sampling.Temperature;
        }

        if (IsValidUnitFloat(sampling.TopP))
        {
            chatOptions.TopP = sampling.TopP;
        }

        if (IsPositive(sampling.TopK))
        {
            chatOptions.TopK = sampling.TopK;
        }

        // Penalties are accepted in [-2, 2] (the UI cap); out-of-range → skip and keep the model default.
        if (IsValidPenalty(sampling.PresencePenalty))
        {
            chatOptions.PresencePenalty = sampling.PresencePenalty;
        }

        if (IsValidPenalty(sampling.FrequencyPenalty))
        {
            chatOptions.FrequencyPenalty = sampling.FrequencyPenalty;
        }

        // Seed floor mirrors the UI: -1 is the "random seed" sentinel, anything below is invalid → skip.
        if (IsValidSeed(sampling.Seed))
        {
            chatOptions.Seed = sampling.Seed;
        }

        if (sampling.Stop is { Count: > 0 } stop)
        {
            var sequences = stop.Where(static value => !string.IsNullOrWhiteSpace(value)).ToList();
            if (sequences.Count > 0)
            {
                chatOptions.StopSequences = sequences;
            }
        }

        // num_ctx is read first so the output cap below can be clamped to a valid context window.
        var numCtx = IsPositive(sampling.NumCtx) ? sampling.NumCtx : null;
        if (numCtx is { } resolvedCtx)
        {
            additionalProperties[SamplingOptionKeys.NumCtx] = resolvedCtx;
        }

        if (sampling.MaxOutputTokens is { } maxOutputTokens && maxOutputTokens > 0)
        {
            // Safety clamp: an output budget larger than the context window would be rejected/truncated by the model.
            var clamped = numCtx is { } window ? Math.Min(maxOutputTokens, window) : maxOutputTokens;
            chatOptions.MaxOutputTokens = clamped;
        }

        if (IsValidUnitFloat(sampling.MinP))
        {
            additionalProperties[SamplingOptionKeys.MinP] = sampling.MinP!.Value;
        }

        if (IsValidRepeatPenalty(sampling.RepeatPenalty))
        {
            additionalProperties[SamplingOptionKeys.RepeatPenalty] = sampling.RepeatPenalty!.Value;
        }

        if (IsValidRepeatLastN(sampling.RepeatLastN))
        {
            additionalProperties[SamplingOptionKeys.RepeatLastN] = sampling.RepeatLastN!.Value;
        }
    }

    /// <summary>One message per set field that <see cref="Apply" /> would skip; empty when every set field is in range.</summary>
    public static IReadOnlyList<string> Validate(InvocationSamplingOptions sampling)
    {
        ArgumentNullException.ThrowIfNull(sampling);

        List<string> errors = [];
        Check(errors, sampling.Temperature, IsValidTemperature, "Temperature must be between 0 and 2.");
        Check(errors, sampling.TopP, IsValidUnitFloat, "Top P must be between 0 and 1.");
        Check(errors, sampling.TopK, IsPositive, "Top K must be greater than 0.");
        Check(errors, sampling.MinP, IsValidUnitFloat, "Min P must be between 0 and 1.");
        Check(errors, sampling.PresencePenalty, IsValidPenalty, "Presence penalty must be between -2 and 2.");
        Check(errors, sampling.FrequencyPenalty, IsValidPenalty, "Frequency penalty must be between -2 and 2.");
        Check(errors, sampling.RepeatPenalty, IsValidRepeatPenalty, "Repeat penalty must be 0 or greater.");
        Check(errors, sampling.RepeatLastN, IsValidRepeatLastN, "Repeat last N must be -1 or greater.");
        Check(errors, sampling.Seed, IsValidSeed, "Seed must be -1 or greater.");
        Check(errors, sampling.NumCtx, IsPositive, "Context size must be greater than 0.");
        Check(errors, sampling.MaxOutputTokens, IsPositive, "Max output tokens must be greater than 0.");
        Check(errors, sampling.ReasoningBudgetTokens, IsPositive, "Reasoning budget must be greater than 0.");
        return errors;
    }

    private static void Check<T>(List<string> errors, T? value, Func<T?, bool> isValid, string message)
        where T : struct
    {
        if (value is not null && !isValid(value))
        {
            errors.Add(message);
        }
    }

    private static bool IsValidTemperature(float? value) =>
        IsValidRangedFloat(value, min: 0f, max: 2f);

    private static bool IsValidPenalty(float? value) =>
        IsValidRangedFloat(value, min: -2f, max: 2f);

    private static bool IsValidRepeatPenalty(float? value) =>
        value is { } resolved && !float.IsNaN(resolved) && resolved >= 0f;

    private static bool IsValidRepeatLastN(int? value) =>
        value >= -1;

    private static bool IsValidSeed(long? value) =>
        value >= -1;

    private static bool IsPositive(int? value) =>
        value > 0;

    private static bool IsValidRangedFloat(float? value, float min, float max)
    {
        return value is { } resolved && !float.IsNaN(resolved) && resolved >= min && resolved <= max;
    }

    private static bool IsValidUnitFloat(float? value)
    {
        return IsValidRangedFloat(value, min: 0f, max: 1f);
    }
}
