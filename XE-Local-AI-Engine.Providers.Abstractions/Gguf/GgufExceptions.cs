namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>Classifies why a Hugging Face GGUF download failed, so callers can surface a clear, sanitized reason.</summary>
public enum HuggingFaceDownloadFailure
{
    /// <summary>Transient or unexpected network/transport failure after retries.</summary>
    Network = 0,

    /// <summary>The repo is gated and no token was configured.</summary>
    Gated = 1,

    /// <summary>A token was sent but rejected (401/403) for this repo.</summary>
    Unauthorized = 2,

    /// <summary>The volume ran out of space mid-download (the partial file is retained for resume).</summary>
    DiskFull = 3,

    /// <summary>The downloaded bytes did not match the expected sha256 (LFS OID).</summary>
    HashMismatch = 4,

    /// <summary>The requested repo, revision, or file was not found.</summary>
    NotFound = 5,

    /// <summary>The final managed destination already exists, including a case-only filesystem collision.</summary>
    DestinationConflict = 6
}

/// <summary>
///     Sanitized, user-facing failure surface for Hugging Face discovery/download. The <see cref="Exception.Message" />
///     is safe to show — it must never carry the HF token, a <c>Bearer</c> value, or internal absolute paths/URLs.
///     Internal diagnostics belong only in the (non-surfaced) inner exception.
/// </summary>
public sealed class HuggingFaceDownloadException : Exception
{
    /// <summary>Creates a sanitized failure with the classified reason.</summary>
    public HuggingFaceDownloadException(HuggingFaceDownloadFailure reason, string sanitizedMessage)
        : base(sanitizedMessage)
    {
        Reason = reason;
    }

    /// <summary>Creates a sanitized failure wrapping an internal cause kept out of the surfaced message.</summary>
    public HuggingFaceDownloadException(HuggingFaceDownloadFailure reason, string sanitizedMessage, Exception innerException)
        : base(sanitizedMessage, innerException)
    {
        Reason = reason;
    }

    /// <summary>The classified failure reason.</summary>
    public HuggingFaceDownloadFailure Reason { get; }
}

/// <summary>
///     Thrown by the store's hard pre-download disk guard when free space on the target volume is below the file size
///     plus the configured margin — before any bytes are written.
/// </summary>
public sealed class InsufficientDiskSpaceException : Exception
{
    /// <summary>Creates the guard failure with the required vs available byte counts.</summary>
    public InsufficientDiskSpaceException(long requiredBytes, long availableBytes)
        : base("There is not enough free disk space to download the selected model.")
    {
        RequiredBytes = requiredBytes;
        AvailableBytes = availableBytes;
    }

    /// <summary>Bytes required (file size + safety margin).</summary>
    public long RequiredBytes { get; }

    /// <summary>Bytes currently free on the target volume.</summary>
    public long AvailableBytes { get; }
}

/// <summary>Signals that acquisition-owned temporary artifacts remained after provider cleanup was attempted.</summary>
public sealed class GgufAcquisitionCleanupException : Exception
{
    /// <summary>Creates a sanitized cleanup failure while retaining internal diagnostics in the inner exception.</summary>
    public GgufAcquisitionCleanupException(string sanitizedMessage, Exception innerException)
        : base(sanitizedMessage, innerException)
    {
    }
}
