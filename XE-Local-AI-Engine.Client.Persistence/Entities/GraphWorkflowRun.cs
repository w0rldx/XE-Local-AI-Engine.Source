namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     One execution of a definition. <see cref="RequestId" /> is the caller-minted idempotency key: the same request
///     id always answers with the same run, which is what lets a later scheduler or integration caller retry a start
///     without risking a second run.
/// </summary>
internal sealed class GraphWorkflowRun
{
    public Guid Id { get; set; }

    /// <summary>Caller-minted and unique, so a retried start resolves to the run the first call created.</summary>
    public Guid RequestId { get; set; }

    /// <summary>No foreign key: a definition may be hard-deleted while terminal runs stand, and those runs keep their pinned graph.</summary>
    public Guid DefinitionId { get; set; }

    public int DefinitionVersion { get; set; }

    /// <summary>Which graph actually ran, so runs group by document without decrypting one.</summary>
    public string GraphHash { get; set; } = string.Empty;

    public GraphWorkflowRunStatus Status { get; set; }
    public GraphWorkflowFailureClass FailureClass { get; set; }

    /// <summary>The executed graph, pinned at start. The definition row is never mutated by a run.</summary>
    public byte[] GraphJson { get; set; } = [];

    /// <summary>The operator's start payload, which the Start node hands downstream.</summary>
    public byte[]? InputJson { get; set; }

    /// <summary>The result the succeeded End node resolved. Written once, at terminalization.</summary>
    public byte[]? OutputJson { get; set; }

    /// <summary>
    ///     The run's single monotonic change watermark. Every node-run and event insert takes a fresh value from here
    ///     inside the transaction that owns the run row, so one number answers "what changed since?" for the subtree.
    /// </summary>
    public long Seq { get; set; }

    public long Version { get; set; }
    public long? CancelRequestedAtUtc { get; set; }
    public long? StartedAtUtc { get; set; }
    public long? CompletedAtUtc { get; set; }
    public long CreatedAtUtc { get; set; }
}
