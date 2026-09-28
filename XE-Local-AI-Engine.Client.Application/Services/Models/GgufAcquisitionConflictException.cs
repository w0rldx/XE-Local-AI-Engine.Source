namespace XE_Local_AI_Engine.Client.Services.Models;

/// <summary>
///     Signals that a GGUF acquisition (download or import) cannot proceed because the canonical model name, its destination files, or its
///     <c>model_provider_map</c> row are already claimed by something else.
/// </summary>
/// <remarks>
///     Typed so the HTTP boundary and the import coordinator map it to 409 without matching on an exception message. The message is
///     sanitized and safe to surface — it never carries a path, URL, or token.
/// </remarks>
public sealed class GgufAcquisitionConflictException : Exception
{
    /// <summary>Creates the conflict with the sanitized, operator-facing message.</summary>
    public GgufAcquisitionConflictException()
        : base("The model name or destination is already in use.")
    {
    }
}
