namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Decrypted, typed projection of a persisted <c>CanvasWorkflow</c>. <see cref="GraphJson" /> is returned in
///     plaintext (decrypted on materialization); the store converts to and from this shape at the boundary so callers
///     never touch the encrypted byte column. A summary read (<c>ListAsync</c>) leaves <see cref="GraphJson" />
///     <c>null</c> — the graph blob is omitted from list projections.
/// </summary>
public sealed record CanvasWorkflowRecord(
    Guid Id,
    string Name,
    string? GraphJson,
    int Version,
    long CreatedAtUtc,
    long UpdatedAtUtc);
