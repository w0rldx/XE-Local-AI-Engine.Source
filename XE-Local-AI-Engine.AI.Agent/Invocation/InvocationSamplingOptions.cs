namespace XE_Local_AI_Engine.AI.Agent.Invocation;

/// <summary>
///     Provider-agnostic mirror of the client-side sampling overrides. Lives in <c>.AI.Agent</c> because that project
///     cannot reference <c>Client.Models</c>; the invocation runner maps the client record onto this one. Every field is
///     optional — a null field means "no override" and the factory leaves the corresponding Ollama option at its model
///     default, keeping the no-override path byte-identical.
/// </summary>
public sealed record InvocationSamplingOptions
{
    public float? Temperature { get; init; }

    public float? TopP { get; init; }

    public int? TopK { get; init; }

    public float? MinP { get; init; }

    public int? MaxOutputTokens { get; init; }

    public float? RepeatPenalty { get; init; }

    public int? RepeatLastN { get; init; }

    public float? PresencePenalty { get; init; }

    public float? FrequencyPenalty { get; init; }

    public long? Seed { get; init; }

    public IReadOnlyList<string>? Stop { get; init; }

    public int? NumCtx { get; init; }
}
