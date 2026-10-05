namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>A launch the capacity gate refused: a policy outcome, not a crash, whose sanitized message names the loaded models.</summary>
/// <remarks>A <see cref="LlamaRuntimeException" />, so every existing runtime catch still handles it; the type lets the invocation classifier tell it apart.</remarks>
public sealed class LlamaCapacityRefusedException : LlamaRuntimeException
{
    /// <summary>Creates a capacity refusal with a user-safe message.</summary>
    public LlamaCapacityRefusedException(string sanitizedMessage)
        : base(sanitizedMessage)
    {
    }
}
