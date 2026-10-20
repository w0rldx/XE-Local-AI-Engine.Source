namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Metadata of one immutable artifact a judging produced. The content lives in an encrypted blob whose id is
///     <see cref="Id" />; this row carries no payload, only what is needed to find and verify it.
/// </summary>
internal sealed record class BenchmarkRunArtifact
{
    public Guid Id { get; set; }

    /// <summary>The blob scope.</summary>
    public Guid RunId { get; set; }

    /// <summary>The judging that produced it. NOT NULL because a SQLite unique index repeats NULLs.</summary>
    public Guid JudgeAttemptId { get; set; }

    /// <summary>Which criterion produced it. NOT NULL for the same reason as <see cref="JudgeAttemptId" />.</summary>
    public string CriterionId { get; set; } = string.Empty;

    /// <summary>A closed set (<c>BenchmarkRunArtifactKinds</c>); a new kind is a table rebuild.</summary>
    public string Kind { get; set; } = string.Empty;

    public string MediaType { get; set; } = string.Empty;
    public long ByteCount { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public long CreatedAtUtc { get; set; }
}
