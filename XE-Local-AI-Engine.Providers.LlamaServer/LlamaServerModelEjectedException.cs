namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Raised when an in-flight inference request fails because the operator force-ejected its model out from under it.
/// </summary>
/// <remarks>
///     Distinct from a generic connection drop, a crash or a runtime switch, so the invocation runner classifies the
///     terminal state as an operator eject and surfaces a truthful message rather than treating it as a provider
///     outage and — worse — retrying the send. The <see cref="Exception.Message" /> is user-safe, carrying no paths,
///     ports or provider internals, only the fact that the model was ejected; the original transport failure is kept
///     as the inner exception.
/// </remarks>
public sealed class LlamaServerModelEjectedException : Exception
{
    /// <summary>Creates the operator-eject failure with a user-safe message and the underlying transport cause.</summary>
    public LlamaServerModelEjectedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
