namespace XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>The one tokens-per-second derivation every measurement path shares.</summary>
/// <remarks>
///     Four byte-identical copies of <c>tokens * 1000 / milliseconds</c> had accumulated — the invocation throughput snapshot, the
///     persisted benchmark throughput, the executor's blended fallback and the inference harness's counter deltas — each with its own
///     guard against a zero or absent duration, which is exactly the guard a fifth copy forgets. It lives in the abstractions
///     assembly, not the application one, because two of those callers — the persistence layer's <c>BenchmarkRunThroughput</c> and the
///     AI-agent boundary — may not reference the application project. Pure arithmetic, no contract of its own.
/// </remarks>
public static class TokenThroughput
{
    /// <summary>Tokens per second from a token count and a duration in MILLISECONDS (the llama-server timings unit).</summary>
    /// <remarks>
    ///     Null in, null out, and a non-positive duration is null rather than infinity: an absent measurement must read
    ///     as absent, never as a rate nobody measured.
    /// </remarks>
    public static double? FromMilliseconds(double? tokens, double? milliseconds) =>
        tokens is { } count && milliseconds is > 0 ? count * 1000d / milliseconds.Value : null;

    /// <summary>
    ///     Tokens per second from a token count and a duration in SECONDS — the unit the Prometheus counters the
    ///     inference harness scrapes are published in, so its deltas need no conversion of their own.
    /// </summary>
    /// <remarks>
    ///     Null in, null out, and a non-positive duration is null rather than infinity: an absent measurement must read
    ///     as absent, never as a rate nobody measured.
    /// </remarks>
    public static double? FromSeconds(double? tokens, double? seconds) =>
        tokens is { } count && seconds is > 0 ? count / seconds.Value : null;
}
