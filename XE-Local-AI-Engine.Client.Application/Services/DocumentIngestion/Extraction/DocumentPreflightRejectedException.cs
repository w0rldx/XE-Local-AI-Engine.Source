namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion.Extraction;

/// <summary>
///     Thrown by the pre-parse preflight when a compressed container's own metadata — the ZIP central directory or the
///     PDF page count — declares bounds exceeding the extraction limits, rejecting it BEFORE the parser materializes it.
/// </summary>
/// <remarks>
///     The message is content-free, carrying declared sizes, ratios and counts only and never any file content, so it is
///     safe to surface to callers as the extraction failure reason.
/// </remarks>
public sealed class DocumentPreflightRejectedException : Exception
{
    public DocumentPreflightRejectedException(string message) : base(message)
    {
    }

    public DocumentPreflightRejectedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
