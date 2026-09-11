namespace XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;

/// <summary>
///     The private carrier a pipeline phase throws once it has decided what the failure MEANS. It exists so that the
///     one <c>catch</c> at the top of every pipeline writes a category that was chosen where the context was — the
///     phase, the service, the ports the attempt held — rather than re-deriving it from an exception type three
///     frames later, where none of that is in scope.
/// </summary>
/// <remarks>
///     Never escapes to a caller: every pipeline turns it into a row state. A refusal a CALLER must see is one of the
///     eleven public exceptions instead.
/// </remarks>
public sealed class ExternalAppPipelineException : Exception
{
    public ExternalAppPipelineException(ExternalAppFailure failure, Exception? innerException = null)
        : base(failure?.Summary ?? "The operation failed.", innerException)
    {
        Failure = failure ?? throw new ArgumentNullException(nameof(failure));
    }

    /// <summary>The category and the content-free summary to persist.</summary>
    public ExternalAppFailure Failure { get; }
}
