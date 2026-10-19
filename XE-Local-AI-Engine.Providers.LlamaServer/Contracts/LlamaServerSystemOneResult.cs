namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>A typed <c>/v1/systemone</c> outcome: the caller needs the failure class, so a failure is never null.</summary>
public sealed class LlamaServerSystemOneResult
{
    public required LlamaServerSystemOneOutcome Outcome { get; init; }

    /// <summary>Set exactly when <see cref="Outcome" /> is <see cref="LlamaServerSystemOneOutcome.Answered" />.</summary>
    public LlamaServerSystemOneAnswer? Answer { get; init; }

    /// <summary>The server's <c>usage.input_tokens</c>; a decision model emits no output tokens.</summary>
    public int? InputTokens { get; init; }

    /// <summary>Wall time of the HTTP round-trip, excluding any process start.</summary>
    public long? DurationMs { get; init; }

    /// <summary>A short diagnostic for the node log (status and the server's own error text, capped); never request text.</summary>
    public string? Detail { get; init; }
}
