namespace XE_Local_AI_Engine.Client.Persistence.Entities;

public enum ModelLabExperimentStatus
{
    Running,
    Succeeded,
    Failed,
    Cancelled
}

/// <summary>One Model Lab job and its evidence. <c>Running</c> rows do not survive a restart.</summary>
internal sealed record class ModelLabExperiment
{
    public Guid Id { get; set; }

    public ModelLabExperimentStatus Status { get; set; }

    public string RepoId { get; set; } = string.Empty;

    /// <summary>The commit the checkpoint was pinned to, kept here because the base artifact can be deleted later.</summary>
    public string Revision { get; set; } = string.Empty;

    /// <summary>Not a foreign key: a saved experiment must not stop an operator deleting a large checkpoint.</summary>
    public Guid BaseArtifactId { get; set; }

    public string InstalledModelName { get; set; } = string.Empty;

    /// <summary>Encrypted UTF-8: the operator's own text.</summary>
    public byte[] Prompt { get; set; } = [];

    /// <summary>Encrypted: what the operator confirmed about the checkpoint's license for this experiment.</summary>
    public byte[] LicenseConfirmationJson { get; set; } = [];

    /// <summary>Encrypted: the generated tokens and the job's measured facts. Set on success only.</summary>
    public byte[]? ResultJson { get; set; }

    /// <summary>Encrypted UTF-8: a script's error can quote what it was given.</summary>
    public byte[]? ErrorMessage { get; set; }

    /// <summary>Encrypted, like a training run's. Cleared on normal completion and by the startup reaper.</summary>
    public byte[]? LaunchReceiptJson { get; set; }

    /// <summary>The trace's blob in the Model Lab store, whose scope is this row's id. Null until the job has stored it.</summary>
    public Guid? TraceBlobId { get; set; }

    public string? TraceSha256 { get; set; }

    public long? TraceByteCount { get; set; }

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }
}
