namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>No llama.cpp runtime is installed and the node's policy refused to download one implicitly.</summary>
public sealed class LlamaRuntimeNotInstalledException : LlamaRuntimeException
{
    /// <summary>Creates the refusal with its user-facing reason.</summary>
    public LlamaRuntimeNotInstalledException(string sanitizedMessage)
        : base(sanitizedMessage)
    {
    }
}
