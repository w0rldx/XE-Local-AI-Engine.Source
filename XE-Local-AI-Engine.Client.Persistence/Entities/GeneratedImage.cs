namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Metadata for one generated image blob. The image bytes themselves live encrypted on disk (AES-256-GCM, node key)
///     under <c>{NodeDataDirectory.Root}/generated-images/{jobId}/</c> — this row only carries the pointer + dimensions,
///     mirroring <c>ConversationUploadedFile</c>. Cascade-deleted with its owning <see cref="ImageJob" />.
/// </summary>
internal sealed record class GeneratedImage
{
    /// <summary>Image identity (PK).</summary>
    public Guid ImageId { get; set; }

    /// <summary>The owning job (FK → <see cref="ImageJob" />, cascade delete).</summary>
    public Guid JobId { get; set; }

    /// <summary>MIME type of the stored image; currently <c>image/png</c>.</summary>
    public string MimeType { get; set; } = string.Empty;

    /// <summary>Image width in pixels.</summary>
    public int Width { get; set; }

    /// <summary>Image height in pixels.</summary>
    public int Height { get; set; }

    /// <summary>Encrypted-at-rest blob size in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Absolute path to the encrypted blob on disk.</summary>
    public string StoragePath { get; set; } = string.Empty;

    /// <summary>When the image was persisted (unix ms UTC).</summary>
    public long CreatedAtUtc { get; set; }
}
