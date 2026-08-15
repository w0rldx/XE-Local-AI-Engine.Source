namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     A downloaded trainable HF checkpoint. Its files live under <c>INodeDataDirectory.Root/training/base/&lt;id&gt;/</c>
///     — derived from <see cref="Id" /> by the service layer, so there is no path column (the image lane keeps per-file
///     paths inside the parts manifest, which here is <see cref="FilesJson" />).
/// </summary>
internal sealed record class TrainingBaseArtifact
{
    public Guid Id { get; set; }

    /// <summary>The Hugging Face repository the checkpoint came from. Plaintext (structural), case-insensitive.</summary>
    public string RepoId { get; set; } = string.Empty;

    /// <summary>The resolved HF commit sha the download was pinned to.</summary>
    public string Revision { get; set; } = string.Empty;

    public TrainingBaseArtifactStatus Status { get; set; }

    /// <summary>
    ///     Role-tagged file manifest as UTF-8 JSON (per-file name, local path, size, SHA-256 — the image-lane parts
    ///     shape). Plaintext while tracked in memory; encrypted at rest by
    ///     <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name
    ///     <c>training_base_files_json</c>. Required.
    /// </summary>
    public byte[] FilesJson { get; set; } = [];

    public long TotalBytes { get; set; }

    /// <summary>
    ///     Fetched license metadata as UTF-8 JSON (license id/url, gated flags, fetch time). Same treatment as
    ///     <see cref="FilesJson" /> under AAD column name <c>training_base_license_json</c>. Optional — a repo without a
    ///     license tag has none, and that absence is itself what the license gate presents.
    /// </summary>
    public byte[]? LicenseJson { get; set; }

    public string? ErrorMessage { get; set; }

    public long Version { get; set; }

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }
}
