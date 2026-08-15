namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     One training run. Everything a run needs to be reproducible is frozen into the row at creation — the exact
///     sample membership, the resolved options, and the license confirmation — so a later dataset edit cannot
///     retroactively change what a finished run was trained on.
/// </summary>
internal sealed record class TrainingRun
{
    public Guid Id { get; set; }

    /// <summary>Source dataset. Real FK to <c>training_datasets.id</c>, restricted delete; indexed.</summary>
    public Guid DatasetId { get; set; }

    /// <summary>
    ///     The dataset's <see cref="TrainingDataset.ContentFingerprint" /> at freeze time, copied inside the creating
    ///     transaction. Plaintext (structural — it is what a re-run compares against).
    /// </summary>
    public string DatasetContentFingerprint { get; set; } = string.Empty;

    /// <summary>The <see cref="TrainingDataset.Revision" /> this run froze against.</summary>
    public int DatasetRevision { get; set; }

    /// <summary>
    ///     The freeze itself as UTF-8 JSON: the exact sample ids and labels, the train/hold-out split, and the SHA-256
    ///     of the frozen blob. Plaintext while tracked in memory; encrypted at rest by
    ///     <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name
    ///     <c>training_run_freeze_json</c>. Required.
    /// </summary>
    public byte[] FreezeJson { get; set; } = [];

    /// <summary>Base checkpoint. Real FK to <c>training_base_artifacts.id</c>, restricted delete; indexed.</summary>
    public Guid BaseArtifactId { get; set; }

    /// <summary>
    ///     The installed GGUF counterpart of the base checkpoint, when the operator picked the base by way of an
    ///     installed model. Lineage only — nothing resolves the run through it, so it is plaintext and nullable.
    /// </summary>
    public string? LinkedInstalledModelName { get; set; }

    /// <summary>Content fingerprint of <see cref="LinkedInstalledModelName" /> at selection time. Lineage only.</summary>
    public string? LinkedModelContentFingerprint { get; set; }

    /// <summary>
    ///     The resolved training options as UTF-8 JSON. Same treatment as <see cref="FreezeJson" /> under AAD column
    ///     name <c>training_run_options_json</c>. Required.
    /// </summary>
    public byte[] OptionsJson { get; set; } = [];

    /// <summary>
    ///     The persisted license confirmation record. Same treatment under AAD column name
    ///     <c>training_run_license_confirmation_json</c>. Nullable only so the column can exist before the gate is
    ///     satisfied; the store refuses to create a run without one.
    /// </summary>
    public byte[]? LicenseConfirmationJson { get; set; }

    public TrainingRunStatus Status { get; set; }

    /// <summary>
    ///     Latest progress snapshot as UTF-8 JSON. Same treatment under AAD column name
    ///     <c>training_run_progress_json</c>. Null until the executor reports for the first time.
    /// </summary>
    public byte[]? ProgressJson { get; set; }

    /// <summary>
    ///     Tail of the trainer's console output as UTF-8 text. Same treatment under AAD column name
    ///     <c>training_run_log_tail</c>. Bounded by the store, not by a CHECK constraint — the column is ciphertext at
    ///     rest, so SQLite cannot see the length being bounded.
    /// </summary>
    public byte[]? LogTail { get; set; }

    /// <summary>
    ///     What the host needs to identify and reap the trainer process: PID, process group id, executable realpath,
    ///     process start time, run token and owned workdir, as UTF-8 JSON. Same treatment under AAD column name
    ///     <c>training_run_launch_receipt_json</c>. Null before launch and after a clean exit.
    /// </summary>
    public byte[]? LaunchReceiptJson { get; set; }

    /// <summary>Sanitized failure message. Plaintext and bounded — it is operator-facing, not trainer output.</summary>
    public string? ErrorMessage { get; set; }

    public long Version { get; set; }

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }
}
