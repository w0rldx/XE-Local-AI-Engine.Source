namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Raised when an in-flight inference request fails because the operator force-ejected its model out from under it.
///     Distinct from a generic connection drop (crash / runtime switch) so the invocation runner can classify the
///     terminal state as an operator eject and surface a truthful message, rather than treating it as a provider
///     outage and (worse) retrying the send.
/// </summary>
/// <remarks>
///     The <see cref="Exception.Message" /> is user-safe: it carries no paths, ports, or provider internals — only the
///     fact that the model was ejected by the operator. The original transport failure is kept as the inner exception.
/// </remarks>
public sealed class LlamaServerModelEjectedException : Exception
{
    /// <summary>Creates the operator-eject failure with a user-safe message and the underlying transport cause.</summary>
    public LlamaServerModelEjectedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
