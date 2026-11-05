namespace XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>Raised when a GPU-load admission wait exceeds the configured max-wait.</summary>
/// <remarks>
///     Surfaced rather than hanging, so a chat turn behind a wedged model load fails with a clear, user-safe message
///     instead of blocking forever. The message is sanitized — no paths, model identities, or internal detail.
/// </remarks>
public sealed class GpuModelLoadAdmissionTimeoutException : Exception
{
    private const string DefaultMessage =
        "The model runtime could not start because another model load did not finish in time. Please try again.";

    /// <summary>Creates the exception with the default sanitized message.</summary>
    public GpuModelLoadAdmissionTimeoutException()
        : base(DefaultMessage)
    {
    }

    /// <summary>Creates the exception with a sanitized <paramref name="message" />.</summary>
    public GpuModelLoadAdmissionTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a sanitized <paramref name="message" /> and an <paramref name="innerException" />.</summary>
    public GpuModelLoadAdmissionTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
