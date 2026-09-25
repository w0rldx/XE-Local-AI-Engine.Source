namespace XE_Local_AI_Engine.Providers.WhisperCpp;

/// <summary>
///     A sanitized, display-safe failure raised by the whisper.cpp runtime infrastructure (binary acquisition, hash
///     verification, extraction, supervision, transcription).
/// </summary>
/// <remarks>
///     Messages never carry internal paths, URLs, or secrets — they are safe to surface directly to the operator.
/// </remarks>
public sealed class WhisperRuntimeException : Exception
{
    /// <summary>Creates the exception with a sanitized, display-safe message.</summary>
    public WhisperRuntimeException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a sanitized, display-safe message and the underlying cause.</summary>
    public WhisperRuntimeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    ///     Whether the daemon process died under the request. Only this failure is worth one retry: the supervisor has
    ///     already torn the dead process down, so the next request respawns it (on CPU after a CUDA death).
    /// </summary>
    public bool ProcessExited { get; init; }
}
