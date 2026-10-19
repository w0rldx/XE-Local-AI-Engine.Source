namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>One single-question <c>/v1/systemone</c> request against a decision-role process.</summary>
public sealed class LlamaServerSystemOneRequest
{
    /// <summary>The installed decision model, which names the <c>(model, Decision)</c> process.</summary>
    public required string ModelName { get; init; }

    /// <summary>The text the question is asked about (the systemone <c>state</c>).</summary>
    public required string State { get; init; }

    public required LlamaServerSystemOneQuestionType QuestionType { get; init; }

    /// <summary>The question itself (the systemone <c>instructions</c>).</summary>
    public required string Instructions { get; init; }

    /// <summary>Choice: the option names. Score: the ordered levels, lowest first. Noul: empty.</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>Choice only: an optional description per option; an option without one is sent as <c>null</c>.</summary>
    public IReadOnlyDictionary<string, string> Criteria { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
