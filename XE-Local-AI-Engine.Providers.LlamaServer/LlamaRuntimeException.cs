namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Sanitized, user-facing failure surface for the llama-server runtime (binary acquisition + process supervision).
/// </summary>
/// <remarks>
///     The <see cref="Exception.Message" /> is safe to show to the user — it must never carry internal absolute paths,
///     download URLs with tokens, or secrets. Any internal diagnostic detail belongs in the (non-surfaced) inner
///     exception, never in the message.
/// </remarks>
public sealed class LlamaRuntimeException : Exception
{
    /// <summary>Creates a sanitized runtime failure with a user-safe message.</summary>
    public LlamaRuntimeException(string sanitizedMessage)
        : base(sanitizedMessage)
    {
    }

    /// <summary>Creates a sanitized runtime failure wrapping an internal cause kept out of the surfaced message.</summary>
    public LlamaRuntimeException(string sanitizedMessage, Exception innerException)
        : base(sanitizedMessage, innerException)
    {
    }
}
