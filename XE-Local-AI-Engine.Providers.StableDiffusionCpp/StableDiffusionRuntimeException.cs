namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp;

/// <summary>
///     A sanitized, display-safe failure raised by the stable-diffusion.cpp runtime infrastructure (binary acquisition,
///     hash verification, extraction). Messages never carry internal paths, URLs, or secrets — they are safe to surface
///     directly to the operator.
/// </summary>
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
}
