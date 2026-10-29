namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

// The workflow designer's turn. The client holds the conversation and sends it whole; the node keeps nothing between turns.

/// <summary>One designer turn over the working graph on the canvas.</summary>
public sealed class RunGraphWorkflowDesignerTurnRequest
{
    /// <summary>The definition the canvas belongs to, for its action flag; omit for a graph not yet saved.</summary>
    public Guid? DefinitionId { get; init; }

    public string ModelName { get; init; } = string.Empty;

    /// <summary>The conversation so far, oldest first; the last message is the operator's.</summary>
    public IReadOnlyList<GraphWorkflowDesignerMessageRequest> Messages { get; init; } = [];

    /// <summary>The working graph, which may be empty or invalid.</summary>
    public GraphWorkflowGraph Graph { get; init; } = GraphWorkflowGraph.Empty;

    public string? SelectedNodeKey { get; init; }

    /// <summary><c>auto</c> (the default), <c>ask</c> (reply only) or <c>change</c> (a proposal is expected).</summary>
    public string? Intent { get; init; }
}

public sealed class GraphWorkflowDesignerMessageRequest
{
    /// <summary><c>user</c> or <c>assistant</c>.</summary>
    public string Role { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;
}

/// <summary>
///     A finished turn. <see cref="Proposal" /> and <see cref="Validation" /> are null when the model only replied; a proposal
///     with <c>validation.valid</c> false is shown but cannot be accepted.
/// </summary>
public sealed class GraphWorkflowDesignerTurnResponse
{
    public required string Reply { get; init; }

    public required GraphWorkflowGraph? Proposal { get; init; }

    public required IReadOnlyList<GraphWorkflowDesignerChangeResponse> Changes { get; init; }

    /// <summary>The proposal's report, in the validate endpoint's shape; the semantic lint findings are among its warnings.</summary>
    public required ValidateGraphWorkflowDefinitionResponse? Validation { get; init; }

    /// <summary>Model calls this turn took, retries included.</summary>
    public required int Attempts { get; init; }

    public required GraphWorkflowDesignerGenerationMetadata GenerationMetadata { get; init; }
}

/// <summary>One element the proposal changes. Members equal to their parser default and positions are no change.</summary>
public sealed class GraphWorkflowDesignerChangeResponse
{
    /// <summary><c>node</c>, <c>edge</c>, or <c>graph</c> (key <c>settings</c>: the graph kind or chat block changed).</summary>
    public required string Kind { get; init; }

    public required string Key { get; init; }

    /// <summary><c>added</c>, <c>removed</c> or <c>modified</c>.</summary>
    public required string Change { get; init; }
}

/// <summary>What produced the turn, after the drafting provenance block: the model and when, plus timings and tokens.</summary>
public sealed class GraphWorkflowDesignerGenerationMetadata
{
    public required string Model { get; init; }

    /// <summary>Stamped server-side when the turn finished (Unix ms).</summary>
    public required long GeneratedAtUtc { get; init; }

    public required long DurationMs { get; init; }

    /// <summary>Summed over every attempt; null when the provider reported none.</summary>
    public required long? InputTokens { get; init; }

    public required long? OutputTokens { get; init; }
}

/// <summary>Why a turn could not be produced, for the states the UI treats specially. Bad input is a plain 400 instead.</summary>
public enum GraphWorkflowDesignerErrorCode
{
    /// <summary>409: a run or a draft holds the node; turns never queue.</summary>
    NodeBusy = 0,

    /// <summary>422: the model returned nothing usable, or the generation budget elapsed.</summary>
    Unparseable = 1,

    /// <summary>403: the model leaves the node and may not read local data; <see cref="GraphWorkflowDesignerErrorResponse.Grant" /> names the grant.</summary>
    CloudLocalDataWithheld = 2,

    /// <summary>502: the model provider refused or failed the request; the message carries its HTTP status only.</summary>
    ProviderFailed = 3
}

/// <summary>Typed failure body; <see cref="Message" /> is a fixed operator-facing string and never carries model text.</summary>
public sealed class GraphWorkflowDesignerErrorResponse
{
    public required GraphWorkflowDesignerErrorCode Code { get; init; }

    public required string Message { get; init; }

    /// <summary>The external-connection grant that would allow the call (<c>LocalData</c>), or null.</summary>
    public required string? Grant { get; init; }
}
