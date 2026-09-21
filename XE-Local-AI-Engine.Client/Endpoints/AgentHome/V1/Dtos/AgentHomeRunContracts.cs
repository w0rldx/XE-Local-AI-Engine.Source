namespace XE_Local_AI_Engine.Client.Endpoints.AgentHome.V1;

/// <summary>Query for <c>GET agent-home/runs</c>; both bounds are clamped in the handler.</summary>
public sealed class ListAgentHomeRunsRequest
{
    public int? Limit { get; init; }

    public int? Offset { get; init; }
}

/// <summary>
///     The route-only request the per-run routes share: delete, log and patch text each take nothing but the id.
/// </summary>
/// <remarks>
///     One type rather than three identical ones, and no body on any of them — the id is the whole request, and a
///     body on a DELETE or a GET would be a shape the API foundation guard refuses anyway.
/// </remarks>
public sealed class AgentHomeRunByIdRequest
{
    public required string RunId { get; init; }
}

/// <summary>
///     Capped text read out of one run's own files.
/// </summary>
/// <remarks>
///     Unlike the run summaries, this IS the model-influenced content: it is what the run wrote, served for an
///     operator to read and never parsed by anything. <see cref="Truncated" /> is part of the answer rather than a
///     detail — text with a gap in it and no note would read as the whole story.
/// </remarks>
public sealed class AgentHomeRunTextResponse
{
    public required string Text { get; init; }

    /// <summary>Whether the file went on past what this route will serve.</summary>
    public required bool Truncated { get; init; }
}

/// <summary>Response envelope for <c>GET agent-home/runs</c>, newest first, with the unpaged total.</summary>
public sealed class ListAgentHomeRunsResponse
{
    public required IReadOnlyList<AgentHomeRunDto> Items { get; init; }

    /// <summary>How many runs exist in total, ignoring paging — what lets the client show a real pager.</summary>
    public required int TotalCount { get; init; }
}

/// <summary>
///     One run in the history list. Node-minted ids, node-defined tokens and sizes only: no host path, no patch
///     content and no command output appears here, and a run whose log cannot be read still gets a row.
/// </summary>
public sealed class AgentHomeRunDto
{
    /// <summary>The node-minted run id, which is also what the patch preview and apply endpoints take.</summary>
    public required string RunId { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>
    ///     <c>unknown</c>, <c>cancelled</c>, or one of the goal statuses (<c>NotRun</c>, <c>Completed</c>,
    ///     <c>ToolCallBudgetExceeded</c>, <c>TimeBudgetExceeded</c>, <c>Failed</c>).
    /// </summary>
    public required string Outcome { get; init; }

    /// <summary>Whether the run exported a patch, i.e. whether "Review and apply changes" has anything to show.</summary>
    public required bool PatchExported { get; init; }

    /// <summary>Files the export recorded, or <see langword="null" /> when the record could not be counted.</summary>
    public int? ChangedFileCount { get; init; }

    /// <summary><c>none</c>, <c>applied</c> or <c>rejected</c>, read from the run's own apply events.</summary>
    public required string ApplyState { get; init; }

    /// <summary>The conversation the run was started from, as an opaque id; <see langword="null" /> when unrecorded.</summary>
    public Guid? ConversationId { get; init; }

    public required long SizeBytes { get; init; }
}
