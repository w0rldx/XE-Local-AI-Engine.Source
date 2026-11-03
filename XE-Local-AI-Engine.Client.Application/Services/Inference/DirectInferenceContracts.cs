namespace XE_Local_AI_Engine.Client.Services.Inference;

using XE_Local_AI_Engine.Client.Models;

/// <summary>The <c>type</c> discriminator of a <see cref="DirectInferenceEvent" />.</summary>
public static class DirectInferenceEventTypes
{
    /// <summary>One increment of answer text and/or reasoning text.</summary>
    public const string Delta = "delta";

    /// <summary>The provider's token usage for the request.</summary>
    public const string Usage = "usage";

    /// <summary>llama-server's own prompt/generation timings; never sent for a model that is not served by llama-server.</summary>
    public const string Timings = "timings";

    /// <summary>Probability entries of the text just streamed; only for a request that set <see cref="DirectInferenceRequest.TopLogprobs" />.</summary>
    public const string Logprobs = "logprobs";

    /// <summary>The terminal event of a request that ran to its end.</summary>
    public const string Done = "done";

    /// <summary>The terminal event of a request that was refused or failed.</summary>
    public const string Error = "error";
}

/// <summary>The <c>errorCode</c> of a refusal that is not a classified provider failure.</summary>
public static class DirectInferenceErrorCodes
{
    /// <summary>A training run, evaluation or export holds the GPU exclusively.</summary>
    public const string GpuWorkExclusive = "GpuWorkExclusive";

    /// <summary>Token probabilities were requested for a model that is not an installed llama.cpp model.</summary>
    public const string LogprobsLocalOnly = "LogprobsLocalOnly";
}

/// <summary>One explicit prompt to one model: no agent, tools, history, persona, memory or knowledge base.</summary>
public sealed class DirectInferenceRequest
{
    /// <summary>The concrete model id, routed exactly like a chat send (local, cloud or <c>ext:</c>).</summary>
    public string? ModelId { get; init; }

    public string? SystemPrompt { get; init; }

    public string? Prompt { get; init; }

    /// <summary>Optional overrides, in the same ranges chat accepts; an out-of-range value is refused, not skipped.</summary>
    public SamplingOptions? SamplingOptions { get; init; }

    /// <summary>Alternatives per probability entry, 1 to 20; null requests none. For an installed llama.cpp model only.</summary>
    public int? TopLogprobs { get; init; }
}

/// <summary>One frame of a Direct Inference stream; <see cref="Type" /> says which members are set.</summary>
public sealed class DirectInferenceEvent
{
    public required string Type { get; init; }

    public string? TextDelta { get; init; }

    public string? ReasoningDelta { get; init; }

    public DirectInferenceUsage? Usage { get; init; }

    public DirectInferenceTimings? Timings { get; init; }

    public IReadOnlyList<DirectInferenceLogprobEntry>? Logprobs { get; init; }

    /// <summary>On <c>done</c>: milliseconds from admission to the end of the stream.</summary>
    public long? ElapsedMs { get; init; }

    /// <summary>On <c>done</c>: milliseconds from admission to the first text or reasoning delta; null when none arrived.</summary>
    public long? FirstTokenMs { get; init; }

    public string? FinishReason { get; init; }

    /// <summary>On <c>error</c>: a <see cref="DirectInferenceErrorCodes" /> value or a <c>FailureCategory</c> name.</summary>
    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }
}

/// <summary>One entry of llama-server's pre-sampling probabilities. Not a token: see <c>LlamaServerLogprobs</c>.</summary>
public sealed class DirectInferenceLogprobEntry
{
    public string? Text { get; init; }

    public float Logprob { get; init; }

    /// <summary>The entry came with reasoning text rather than answer text.</summary>
    public bool IsReasoning { get; init; }

    public IReadOnlyList<DirectInferenceLogprobAlternative> Alternatives { get; init; } = [];
}

/// <summary>One alternative of a <see cref="DirectInferenceLogprobEntry" />, most likely first.</summary>
public sealed class DirectInferenceLogprobAlternative
{
    public string? Text { get; init; }

    public float Logprob { get; init; }
}

/// <summary>The provider-reported token usage; any member the provider did not report is null.</summary>
public sealed class DirectInferenceUsage
{
    public long? InputTokens { get; init; }

    public long? OutputTokens { get; init; }

    public long? TotalTokens { get; init; }

    public long? ReasoningTokens { get; init; }

    public long? CachedInputTokens { get; init; }
}

/// <summary>llama-server's per-request timings, as read off the final streamed chunk.</summary>
public sealed class DirectInferenceTimings
{
    public int? PromptTokens { get; init; }

    public double? PromptMs { get; init; }

    public int? GenerationTokens { get; init; }

    public double? GenerationMs { get; init; }

    public int? CachedPromptTokens { get; init; }
}
