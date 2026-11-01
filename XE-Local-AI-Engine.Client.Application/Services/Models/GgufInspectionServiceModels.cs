namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>The model name names no installed GGUF, so there is no file to inspect.</summary>
public sealed class InstalledGgufModelNotFoundException : ResourceNotFoundException
{
    public InstalledGgufModelNotFoundException(string modelName) : base($"No installed GGUF model is registered as '{modelName}'.")
    {
    }
}
