namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Decisions;

/// <summary>One question for a native decision provider, already gated and bound by the executor.</summary>
internal sealed class NativeDecisionRequest
{
    public required string ModelName { get; init; }

    /// <summary>The bound input text the question is asked about.</summary>
    public required string State { get; init; }

    public required GraphWorkflowDecisionModelConfig Config { get; init; }
}
