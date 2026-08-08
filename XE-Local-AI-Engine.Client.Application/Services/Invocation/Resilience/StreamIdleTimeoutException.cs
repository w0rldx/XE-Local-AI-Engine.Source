namespace XE_Local_AI_Engine.Client.Services.Invocation.Resilience;

/// <summary>
///     Raised when a streamed provider response stalls: no chunk arrived within the configured inter-chunk idle
///     window. Derives from <see cref="TimeoutException" /> so the invocation runner's failure classifier maps it to
///     the timeout failure category without a bespoke arm. The message names the timeout that fired and carries no
///     paths, URLs, or provider internals.
/// </summary>
public sealed class StreamIdleTimeoutException : TimeoutException
{
    public StreamIdleTimeoutException(string message)
        : base(message)
    {
    }
}
