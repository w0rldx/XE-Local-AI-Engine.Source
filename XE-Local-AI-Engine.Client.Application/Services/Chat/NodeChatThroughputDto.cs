namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     How fast an assistant turn arrived: time to first token and the prompt/generation split of tokens and milliseconds, mirrored
///     from <see cref="InvocationThroughput" />. Numbers only; every member is null when the provider reported none.
/// </summary>
public sealed record NodeChatThroughputDto
{
    public double? TimeToFirstTokenMs { get; init; }

    public int? PromptTokens { get; init; }

    public double? PromptMs { get; init; }

    public int? GenerationTokens { get; init; }

    public double? GenerationMs { get; init; }

    public int? CachedPromptTokens { get; init; }

    /// <summary>The DTO of <paramref name="throughput" />, or null when there is nothing to carry.</summary>
    public static NodeChatThroughputDto? From(InvocationThroughput? throughput)
    {
        return throughput is null || throughput.IsEmpty
            ? null
            : new NodeChatThroughputDto
            {
                TimeToFirstTokenMs = throughput.TimeToFirstTokenMs,
                PromptTokens = throughput.PromptTokens,
                PromptMs = throughput.PromptMs,
                GenerationTokens = throughput.GenerationTokens,
                GenerationMs = throughput.GenerationMs,
                CachedPromptTokens = throughput.CachedPromptTokens
            };
    }
}
