namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion.Extraction;

/// <summary>
///     Thrown by the pre-parse preflight when a compressed container's own metadata — the ZIP central directory or the
///     PDF page count — declares bounds that exceed the extraction limits, so the container is rejected BEFORE the
///     parser decompresses/materializes it. The message is content-free (declared sizes, ratios, and counts only, never
///     any file content), so it is safe to surface to callers as the extraction failure reason.
/// </summary>
public sealed class DocumentPreflightRejectedException : Exception
{
    public DocumentPreflightRejectedException(string message) : base(message)
    {
    }

    public DocumentPreflightRejectedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
