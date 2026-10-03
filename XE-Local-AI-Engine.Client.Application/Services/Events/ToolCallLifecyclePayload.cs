namespace XE_Local_AI_Engine.Client.Services.Events;

using Microsoft.Extensions.AI;

/// <summary>
///     A single tool-call lifecycle transition for an invocation. Carries the minimal shape the local chat stream
///     needs to surface <c>tool-call-requested</c> and <c>tool-call-completed</c> events: the requested phase fills
///     <see cref="Arguments" />/<see cref="RequiresApproval" />, the completed phase fills <see cref="Result" />/
///     <see cref="IsError" />.
/// </summary>
public sealed record ToolCallLifecyclePayload
{
    public required Guid InvocationId { get; init; }

    public required string ToolCallId { get; init; }

    public required string ToolName { get; init; }

    public required ToolCallLifecyclePhase Phase { get; init; }

    public string? Arguments { get; init; }

    public bool RequiresApproval { get; init; }

    public string? Result { get; init; }

    public bool IsError { get; init; }

    /// <summary>
    ///     The streamed-content length when a <see cref="ToolCallLifecyclePhase.Requested" /> call was reported; the
    ///     chat stream splits the text before it off as an interim segment. Null for the completed phase.
    /// </summary>
    public int? ContentOffset { get; init; }

    /// <summary>
    ///     The binary blocks of a completed result (an MCP image, for instance), which <see cref="Result" /> names only by
    ///     a placeholder. In-process only: never serialized onto the stream.
    /// </summary>
    public IReadOnlyList<DataContent> Media { get; init; } = [];
}

/// <summary>
///     Enumerates supported tool call lifecycle phase values.
/// </summary>
public enum ToolCallLifecyclePhase
{
    Requested = 0,
    Completed = 1
}
