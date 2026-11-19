namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>A load the supervisor refused, or a finished load it did not publish, because the operator ejected that model and no user request has reloaded it.</summary>
/// <remarks>A <see cref="LlamaRuntimeException" />, so every existing runtime catch still handles it; the type lets callers treat it as a skip, not a failure.</remarks>
public sealed class OperatorEjectedException : LlamaRuntimeException
{
    /// <summary>Creates the refusal with a user-safe message.</summary>
    public OperatorEjectedException(string sanitizedMessage)
        : base(sanitizedMessage)
    {
    }
}
