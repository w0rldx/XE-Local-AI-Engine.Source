namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>A launch refused because the named model is not installed on this node: no retry or eviction can change that.</summary>
/// <remarks>A <see cref="LlamaRuntimeException" />, so every existing runtime catch still handles it; the type lets the invocation classifier tell it apart.</remarks>
public sealed class LlamaModelNotInstalledException : LlamaRuntimeException
{
    /// <summary>Creates a not-installed refusal with a user-safe message.</summary>
    public LlamaModelNotInstalledException(string sanitizedMessage)
        : base(sanitizedMessage)
    {
    }
}
