namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Designer;

/// <summary>
///     One turn of the workflow designer: the operator talks to a model about the graph on the canvas, and the model
///     answers with a reply and, when asked for a change, a whole proposed graph.
/// </summary>
/// <remarks>
///     It PROPOSES and writes nothing: the proposal is validated and linted here, the editor shows it as a diff, and only
///     the operator's accept and save change a definition. Never contends with a live run (the drafting admission gate).
/// </remarks>
public interface IGraphWorkflowDesignerService
{
    Task<GraphWorkflowDesignerResult> TurnAsync(GraphWorkflowDesignerTurnRequest request, CancellationToken cancellationToken = default);
}

/// <summary>What the operator wants from this turn. <see cref="Auto" /> lets the model decide whether a change was asked for.</summary>
public enum GraphWorkflowDesignerIntent
{
    Auto = 0,
    Ask = 1,
    Change = 2
}

public enum GraphWorkflowDesignerRole
{
    User = 0,
    Assistant = 1
}

/// <summary>One message of the conversation, which the client holds and sends whole on every turn.</summary>
public sealed class GraphWorkflowDesignerMessage
{
    public required GraphWorkflowDesignerRole Role { get; init; }

    public required string Text { get; init; }
}

public sealed class GraphWorkflowDesignerTurnRequest
{
    /// <summary>The longest message a turn carries. A reply is capped to it too, since the client replays replies as messages.</summary>
    public const int MaxMessageLength = 8000;

    /// <summary>The definition the graph belongs to, for its <c>AllowsActions</c> flag; null judges action tools as not allowed.</summary>
    public Guid? DefinitionId { get; init; }

    public required string ModelName { get; init; }

    public required IReadOnlyList<GraphWorkflowDesignerMessage> Messages { get; init; }

    /// <summary>The working graph on the canvas, as its stored JSON document; it may be empty or invalid.</summary>
    public required string GraphJson { get; init; }

    public string? SelectedNodeKey { get; init; }

    public GraphWorkflowDesignerIntent Intent { get; init; }
}

/// <summary>One element the proposal changes against the working graph. A record because a change list is compared by value.</summary>
public sealed record GraphWorkflowDesignerChange
{
    /// <summary><c>node</c>, <c>edge</c>, or <c>graph</c> with key <c>settings</c> for the graph kind and chat block.</summary>
    public required string Kind { get; init; }

    public required string Key { get; init; }

    /// <summary><c>added</c>, <c>removed</c> or <c>modified</c>.</summary>
    public required string Change { get; init; }
}

/// <summary>A finished turn. <see cref="ProposalGraphJson" />, <see cref="Validation" /> and the changes are empty when the model only replied.</summary>
public sealed class GraphWorkflowDesignerTurn
{
    public required string Reply { get; init; }

    public required string? ProposalGraphJson { get; init; }

    public required IReadOnlyList<GraphWorkflowDesignerChange> Changes { get; init; }

    /// <summary>The proposal's report, as the validate endpoint gives it; null without a proposal.</summary>
    public required GraphWorkflowValidationResult? Validation { get; init; }

    /// <summary>Model calls this turn took, retries included.</summary>
    public required int Attempts { get; init; }

    public required string Model { get; init; }

    public required DateTimeOffset GeneratedAtUtc { get; init; }

    public required long DurationMs { get; init; }

    public required long? InputTokens { get; init; }

    public required long? OutputTokens { get; init; }
}

/// <summary>Why a turn did not happen. The endpoint maps these to status codes.</summary>
public enum GraphWorkflowDesignerFailureKind
{
    /// <summary>A required member is missing or the request exceeds the prompt budget. 400.</summary>
    InvalidRequest = 0,

    /// <summary>The model is neither an installed node-local chat model nor a model the cloud path serves. 400.</summary>
    ModelNotEligible = 1,

    /// <summary>A run or a draft holds the node; turns never queue. 409.</summary>
    NodeBusy = 2,

    /// <summary>The model returned nothing usable, or the generation budget elapsed. 422.</summary>
    Unparseable = 3,

    /// <summary>The model leaves the node and neither its connection's local-data grant nor the node switch allows the workflow to reach it. 403.</summary>
    CloudLocalDataWithheld = 4,

    /// <summary>The model provider refused or failed the request (an HTTP error from llama-server or a cloud endpoint). 502.</summary>
    ProviderFailed = 5
}

/// <summary>Exactly one of <see cref="Turn" /> or <see cref="Failure" /> is set; <see cref="FailureMessage" /> never carries model text.</summary>
public sealed record GraphWorkflowDesignerResult
{
    private GraphWorkflowDesignerResult(GraphWorkflowDesignerTurn? turn, GraphWorkflowDesignerFailureKind? failure, string? failureMessage)
    {
        Turn = turn;
        Failure = failure;
        FailureMessage = failureMessage;
    }

    public GraphWorkflowDesignerTurn? Turn { get; }

    public GraphWorkflowDesignerFailureKind? Failure { get; }

    public string? FailureMessage { get; }

    public static GraphWorkflowDesignerResult Success(GraphWorkflowDesignerTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        return new GraphWorkflowDesignerResult(turn, failure: null, failureMessage: null);
    }

    public static GraphWorkflowDesignerResult Failed(GraphWorkflowDesignerFailureKind kind, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new GraphWorkflowDesignerResult(turn: null, kind, message);
    }
}
