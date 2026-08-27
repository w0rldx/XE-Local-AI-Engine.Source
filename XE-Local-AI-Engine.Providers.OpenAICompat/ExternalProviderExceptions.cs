namespace XE_Local_AI_Engine.Providers.OpenAICompat;

/// <summary>
///     Thrown when a caller asks an external OpenAI-compatible model for a LIFECYCLE operation that only a node-local
///     runtime can perform — pulling weights or deleting them. The node does not own an external model's files; the
///     remote server does.
/// </summary>
/// <remarks>
///     A distinct, typed exception (rather than <see cref="NotSupportedException" />) so the endpoint layer can map it
///     to a clean 409 Conflict — "this model is managed on its connection, not here" — instead of surfacing a 500 for
///     what is a correct refusal. Warm/unload are deliberately NOT in this category: they are benign no-ops, because
///     the keep-warm background service calls warm generically for whatever model is selected and must not start
///     failing the moment an external model becomes the default.
/// </remarks>
public sealed class ExternalProviderOperationNotSupportedException : InvalidOperationException
{
    public ExternalProviderOperationNotSupportedException()
        : base("This operation is not supported for external OpenAI-compatible models.")
    {
    }

    public ExternalProviderOperationNotSupportedException(string message)
        : base(message)
    {
    }

    public ExternalProviderOperationNotSupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
///     Thrown when a chat send names an <c>ext:</c> model the registry cannot resolve — a malformed id, a connection
///     the operator deleted, or a model unregistered while the turn was in flight.
/// </summary>
/// <remarks>
///     This is the FAIL-CLOSED terminal of the external routing path. An unresolvable external id must never fall back
///     to "some other connection" or to a default endpoint: the operator's locality declaration is what decides whether
///     the prompt may leave the node at all, and without a resolved registration there is no such declaration to honour.
///     The message is deliberately sanitized — it names no base URL, key, or connection internals.
/// </remarks>
public sealed class ExternalProviderModelUnavailableException : InvalidOperationException
{
    public ExternalProviderModelUnavailableException()
        : base("The selected external model is no longer registered on any connection.")
    {
    }

    public ExternalProviderModelUnavailableException(string message)
        : base(message)
    {
    }

    public ExternalProviderModelUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
