namespace XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     The separated throughput facts of one completed turn: how long the caller waited for the first token, and how
///     the turn's tokens and milliseconds split between prompt processing (pp) and generation (tg). Every member is
///     nullable and every member is <see langword="null" /> for a provider that reports none — only llama-server puts
///     the underlying <c>timings</c> object on its stream, so a cloud turn carries only
///     <see cref="TimeToFirstTokenMs" /> (measured client-side) and nothing else.
///     <para>
///         Why this exists at all: the blended <c>totalTokens / wall-clock</c> figure the benchmark used to report
///         conflates prefill with decode, so the same model measured on a long prompt and a short one produced two
///         incomparable numbers. pp and tg are the two figures <c>llama-bench</c> reports separately for exactly that
///         reason.
///     </para>
/// </summary>
/// <param name="TimeToFirstTokenMs">
///     Wall-clock milliseconds from turn start to the first emitted chunk, measured client-side — so it includes
///     network, adapter and deserialization overhead on top of the server's own <see cref="PromptMs" />. That is
///     deliberate: it is what a caller actually waits. On a multi-segment (tool-calling) turn this is the FIRST
///     request's latency, since that is when the caller first saw output.
/// </param>
/// <param name="PromptTokens">Prompt tokens evaluated, summed across every provider request the turn made.</param>
/// <param name="PromptMs">Milliseconds spent on prompt processing, summed across every request.</param>
/// <param name="GenerationTokens">Tokens decoded, summed across every request.</param>
/// <param name="GenerationMs">Milliseconds spent decoding, summed across every request.</param>
/// <param name="CachedPromptTokens">
///     Prompt tokens served from the prompt cache rather than evaluated, summed across every request. Non-zero means
///     <see cref="PromptMs" /> is not a cold-prefill measurement — on a tool-calling turn the later requests re-send
///     the whole conversation, and the runtime serves the shared prefix from cache.
/// </param>
/// <param name="SegmentCount">
///     How many provider requests the turn made, i.e. how many readings the sums above are made of. Zero when nothing
///     reported timings, 1 for a plain turn, more once tools are called.
/// </param>
public sealed record InvocationThroughput(
    double? TimeToFirstTokenMs = null,
    int? PromptTokens = null,
    double? PromptMs = null,
    int? GenerationTokens = null,
    double? GenerationMs = null,
    int? CachedPromptTokens = null,
    int SegmentCount = 0)
{
    /// <summary>True when every member is absent, i.e. there is nothing worth carrying or persisting.</summary>
    public bool IsEmpty =>
        TimeToFirstTokenMs is null
        && PromptTokens is null
        && PromptMs is null
        && GenerationTokens is null
        && GenerationMs is null
        && CachedPromptTokens is null
        && SegmentCount == 0;

    /// <summary>
    ///     Decode throughput in tokens per second — the figure <c>llama-bench</c> calls tg. Null unless the provider
    ///     reported both a token count and a decode duration.
    /// </summary>
    public double? GenerationTokensPerSecond => GenerationTokens is { } tokens && GenerationMs is > 0 ? tokens * 1000d / GenerationMs.Value : null;

    /// <summary>
    ///     Prompt-processing throughput in tokens per second — the figure <c>llama-bench</c> calls pp. Null unless the
    ///     provider reported both a token count and a prefill duration.
    /// </summary>
    public double? PromptTokensPerSecond => PromptTokens is { } tokens && PromptMs is > 0 ? tokens * 1000d / PromptMs.Value : null;
}
