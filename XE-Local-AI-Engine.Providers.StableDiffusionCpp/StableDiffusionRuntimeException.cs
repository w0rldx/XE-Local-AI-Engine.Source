namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp;

/// <summary>
///     A sanitized, display-safe failure raised by the stable-diffusion.cpp runtime infrastructure (binary acquisition,
///     hash verification, extraction).
/// </summary>
/// <remarks>
///     Messages never carry internal paths, URLs, or secrets — they are safe to surface directly to the operator.
/// </remarks>
public sealed class StableDiffusionRuntimeException : Exception
{
    /// <summary>Creates the exception with a sanitized, display-safe message.</summary>
    public StableDiffusionRuntimeException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a sanitized, display-safe message and the underlying cause.</summary>
    public StableDiffusionRuntimeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    ///     Whether the <c>sd-server</c> daemon died under the generation. The message then names that, and the next
    ///     generation respawns the daemon.
    /// </summary>
    public bool ProcessExited { get; init; }

    /// <summary>Whether the daemon's stderr showed a GPU out-of-memory. The message is then a fixed, display-safe instruction.</summary>
    public bool OutOfMemory { get; init; }

    /// <summary>
    ///     Whether the running daemon build does not support the requested edit mode. The message is then a fixed,
    ///     display-safe statement of that.
    /// </summary>
    public bool FeatureUnsupported { get; init; }
}
