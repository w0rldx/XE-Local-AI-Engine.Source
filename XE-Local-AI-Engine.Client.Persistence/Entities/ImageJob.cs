namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     A persisted local image-generation job (create → status/progress → cancel → retrieve). Node-scoped. The
///     <see cref="Prompt" />/<see cref="NegativePrompt" /> are stored encrypted at rest (AES-256-GCM, node key) exactly
///     like other sensitive columns — see <c>NodeEncryptionSaveChangesInterceptor</c> /
///     <c>NodeEncryptionMaterializationInterceptor</c> (AAD column names <c>image_prompt</c> / <c>image_negative_prompt</c>).
/// </summary>
internal sealed record class ImageJob
{
    /// <summary>Job identity (PK).</summary>
    public Guid Id { get; set; }

    /// <summary>Canonical image-model name the job generates with.</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>
    ///     UTF-8 prompt bytes. Plaintext while tracked in memory; encrypted at rest by the node encryption interceptor
    ///     using AAD column name <c>image_prompt</c>.
    /// </summary>
    public byte[] Prompt { get; set; } = [];

    /// <summary>
    ///     UTF-8 negative-prompt bytes, or <see langword="null" />. Plaintext while tracked; encrypted at rest using AAD
    ///     column name <c>image_negative_prompt</c>.
    /// </summary>
    public byte[]? NegativePrompt { get; set; }

    /// <summary>Diffusion seed (-1 = random at generation time).</summary>
    public long Seed { get; set; }

    /// <summary>Requested output width in pixels.</summary>
    public int Width { get; set; }

    /// <summary>Requested output height in pixels.</summary>
    public int Height { get; set; }

    /// <summary>Requested sampling steps.</summary>
    public int Steps { get; set; }

    /// <summary>Requested sampler / sampling method.</summary>
    public string Sampler { get; set; } = string.Empty;

    /// <summary>Requested classifier-free-guidance scale.</summary>
    public double CfgScale { get; set; }

    /// <summary>Current lifecycle status.</summary>
    public ImageJobStatus Status { get; set; }

    /// <summary>When the job was created (unix ms UTC).</summary>
    public long CreatedAtUtc { get; set; }

    /// <summary>When generation started (unix ms UTC), or <see langword="null" /> while queued.</summary>
    public long? StartedAtUtc { get; set; }

    /// <summary>When generation reached a terminal state (unix ms UTC), or <see langword="null" />.</summary>
    public long? CompletedAtUtc { get; set; }

    /// <summary>Wall-clock generation duration in milliseconds once complete, or <see langword="null" />.</summary>
    public long? DurationMs { get; set; }

    /// <summary>The produced image's id once persisted, or <see langword="null" />.</summary>
    public Guid? ImageId { get; set; }

    /// <summary>A display-safe failure reason when <see cref="Status" /> is <see cref="ImageJobStatus.Failed" />.</summary>
    public string? SanitizedError { get; set; }

    /// <summary>When a cancel was requested (unix ms UTC), or <see langword="null" />.</summary>
    public long? CancellationRequestedAtUtc { get; set; }
}
