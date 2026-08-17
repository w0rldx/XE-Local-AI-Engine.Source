namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

/// <summary>
///     llama-server's own per-request generation timings, lifted off a streamed chat-completion chunk.
///     <para>
///         llama-server puts a <c>timings</c> object on the FINAL SSE chunk of every OpenAI-compatible streaming chat
///         completion, unconditionally — no request flag, no <c>--metrics</c>, no <c>timings_per_token</c> (that switch
///         only adds it to INTERMEDIATE chunks as well). The numbers are the server's own internal timers, so they
///         separate prompt processing (<c>prompt_n</c>/<c>prompt_ms</c> — what <c>llama-bench</c> calls pp) from token
///         generation (<c>predicted_n</c>/<c>predicted_ms</c> — tg) exactly, which a single client-side wall clock over
///         the whole turn cannot.
///     </para>
///     <para>
///         <c>timings</c> is not part of OpenAI's schema, so it does not reach the typed SDK surface. It is read through
///         <see cref="StreamingChatCompletionUpdate" />'s <c>Patch</c> — the SDK's catch-all for unmodelled JSON —
///         which is marked experimental (<c>SCME0001</c>), hence the local suppression. A provider that sends no
///         <c>timings</c> (any cloud provider, Ollama) yields <see langword="null" /> rather than an exception.
///     </para>
/// </summary>
/// <param name="PromptTokens">Prompt tokens the server evaluated (<c>prompt_n</c>), cached ones included.</param>
/// <param name="PromptMs">Milliseconds spent on prompt processing (<c>prompt_ms</c>).</param>
/// <param name="GenerationTokens">Tokens the server decoded (<c>predicted_n</c>).</param>
/// <param name="GenerationMs">Milliseconds spent decoding (<c>predicted_ms</c>).</param>
/// <param name="CachedPromptTokens">
///     Prompt tokens served from the KV cache instead of being evaluated (<c>cache_n</c>). Zero for a genuinely cold
///     prefill; a non-zero value on a repeat of the same prompt means the pp number is NOT a cold-prefill measurement.
/// </param>
public sealed record LlamaServerGenerationTimings(
    int? PromptTokens,
    double? PromptMs,
    int? GenerationTokens,
    double? GenerationMs,
    int? CachedPromptTokens)
{
    /// <summary>
    ///     Reads the timings off one streamed update's raw representation, or <see langword="null" /> when this update
    ///     carries none. Accepts the raw representation of either a Microsoft.Extensions.AI
    ///     <c>ChatResponseUpdate</c> or an Agent-Framework <c>AgentResponseUpdate</c>: the agent update wraps the chat
    ///     update, whose own raw representation is the OpenAI SDK chunk, so both hops are followed here (verified
    ///     against the pinned Microsoft.Agents.AI 1.17.0 / Microsoft.Extensions.AI.OpenAI 10.9.0 / OpenAI 2.12.0).
    ///     Non-throwing by contract — a missing or malformed field yields <see langword="null" /> members.
    /// </summary>
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
        var timings = new LlamaServerGenerationTimings(promptTokens,
            NonNegative(ReadDouble(ref patch, "$.timings.prompt_ms"u8)),
            generationTokens,
            NonNegative(ReadDouble(ref patch, "$.timings.predicted_ms"u8)),
            NonNegative(ReadInt(ref patch, "$.timings.cache_n"u8)));
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

    // Verified against the pinned SDK, and NOT interchangeable with the obvious alternative: JsonPatch.Contains only
    // answers for paths the patch itself tracks, so Contains("$.timings.prompt_n") is FALSE even on a chunk whose
    // GetInt32 for that exact path returns 123. Guarding each field with Contains therefore reads every timing as
    // absent and silently turns the whole feature off. The absent case is signalled by KeyNotFoundException instead,
    // which is why these are catch-based; the outer Contains("$.timings") guard keeps that off the per-chunk path for
    // providers that send no timings at all.
#pragma warning disable SCME0001
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

    private static int? NonNegative(int? value) => value >= 0 ? value : null;

    private static double? NonNegative(double? value) => value >= 0 ? value : null;
}
