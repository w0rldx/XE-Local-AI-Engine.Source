namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

/// <summary>llama-server's own per-request generation timings, lifted off a streamed chat-completion chunk.</summary>
/// <remarks>
///     llama-server puts a <c>timings</c> object on the FINAL SSE chunk of every OpenAI-compatible streaming chat
///     completion unconditionally — no request flag, no <c>--metrics</c>, no <c>timings_per_token</c>, that switch only
///     adding it to INTERMEDIATE chunks too. They are the server's own internal timers, so they separate prompt
///     processing (<c>prompt_n</c>/<c>prompt_ms</c>, what <c>llama-bench</c> calls pp) from token generation
///     (<c>predicted_n</c>/<c>predicted_ms</c>, tg) exactly, which one client-side wall clock over the turn cannot.
/// </remarks>
public sealed class LlamaServerGenerationTimings
{
    /// <summary>Prompt tokens the server evaluated (<c>prompt_n</c>), excluding cached tokens.</summary>
    public required int? PromptTokens { get; init; }

    /// <summary>Milliseconds spent on prompt processing (<c>prompt_ms</c>).</summary>
    public required double? PromptMs { get; init; }

    /// <summary>Tokens the server decoded (<c>predicted_n</c>).</summary>
    public required int? GenerationTokens { get; init; }

    /// <summary>Milliseconds spent decoding (<c>predicted_ms</c>).</summary>
    public required double? GenerationMs { get; init; }

    /// <summary>
    ///     Prompt tokens served from the KV cache instead of being evaluated (<c>cache_n</c>). Zero for a genuinely cold
    ///     prefill; a non-zero value on a repeat of the same prompt means the pp number is NOT a cold-prefill measurement.
    /// </summary>
    public required int? CachedPromptTokens { get; init; }

    /// <summary>
    ///     Reads the timings off one streamed update's raw representation, or <see langword="null" /> when this update
    ///     carries none. Non-throwing: a missing or malformed field yields <see langword="null" /> members.
    /// </summary>
    /// <remarks>
    ///     It accepts the raw representation of either a Microsoft.Extensions.AI <c>ChatResponseUpdate</c> or an
    ///     Agent-Framework <c>AgentResponseUpdate</c> — the agent update wraps the chat update, whose own raw
    ///     representation is the OpenAI SDK chunk, so both hops are followed (verified at Microsoft.Agents.AI 1.20.0,
    ///     Microsoft.Extensions.AI.OpenAI 10.9.0, OpenAI 2.12.0). <c>timings</c> is outside OpenAI's schema, so it is
    ///     read through <see cref="StreamingChatCompletionUpdate" />'s experimental <c>Patch</c> (<c>SCME0001</c>).
    /// </remarks>
    public static LlamaServerGenerationTimings? TryRead(object? rawRepresentation)
    {
        var update = Unwrap(rawRepresentation);
        if (update is null)
        {
            return null;
        }

#pragma warning disable SCME0001 // JsonPatch is the only route to a JSON field the OpenAI schema does not model.
        ref var patch = ref update.Patch;
        if (!patch.Contains("$.timings"u8))
        {
            return null;
        }

        // prompt_n is -1 on a result the server did not time; treat that as absent rather than persisting a negative
        // token count. Everything else is read independently so a future field removal degrades one member, not all.
        var promptTokens = NonNegative(ReadInt(ref patch, "$.timings.prompt_n"u8));
        var generationTokens = NonNegative(ReadInt(ref patch, "$.timings.predicted_n"u8));
        var timings = new LlamaServerGenerationTimings
        {
            PromptTokens = promptTokens,
            PromptMs = NonNegative(ReadDouble(ref patch, "$.timings.prompt_ms"u8)),
            GenerationTokens = generationTokens,
            GenerationMs = NonNegative(ReadDouble(ref patch, "$.timings.predicted_ms"u8)),
            CachedPromptTokens = NonNegative(ReadInt(ref patch, "$.timings.cache_n"u8))
        };
#pragma warning restore SCME0001

        return timings.PromptTokens is null && timings.GenerationTokens is null ? null : timings;
    }

    private static StreamingChatCompletionUpdate? Unwrap(object? rawRepresentation) =>
        rawRepresentation switch
        {
            StreamingChatCompletionUpdate update => update,
            ChatResponseUpdate chatUpdate => Unwrap(chatUpdate.RawRepresentation),
            _ => null
        };

#pragma warning disable SCME0001
    /// <summary>Reads one integer timing field, or <see langword="null" /> when the chunk does not carry it.</summary>
    /// <remarks>
    ///     Catch-based on purpose and NOT interchangeable with the obvious alternative: verified against the pinned
    ///     SDK, <c>JsonPatch.Contains</c> only answers for paths the patch itself tracks, so
    ///     <c>Contains("$.timings.prompt_n")</c> is FALSE even where <c>GetInt32</c> for that path returns a value, and
    ///     guarding each field with it reads every timing as absent. Absence arrives as <c>KeyNotFoundException</c>;
    ///     the outer <c>"$.timings"</c> guard keeps that off the per-chunk path for providers that send none.
    /// </remarks>
    private static int? ReadInt(ref JsonPatch patch, ReadOnlySpan<byte> path)
    {
        try
        {
            return patch.GetInt32(path);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static double? ReadDouble(ref JsonPatch patch, ReadOnlySpan<byte> path)
    {
        try
        {
            return patch.GetDouble(path);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }
#pragma warning restore SCME0001

    private static int? NonNegative(int? value) =>
        value >= 0 ? value : null;

    private static double? NonNegative(double? value) =>
        value >= 0 ? value : null;
}
