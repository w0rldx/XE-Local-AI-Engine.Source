namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>Writes the operator's kind override for a local model and reports the resulting classification.</summary>
public interface IModelClassificationService
{
    /// <summary>Sets the operator override for <paramref name="modelName" /> and returns its resolved classification.</summary>
    Task<ModelClassificationResult> SetOverrideAsync(string modelName, ModelKind kind, CancellationToken cancellationToken = default);

    /// <summary>Clears the operator override for <paramref name="modelName" /> so the effective kind falls back to the detected one.</summary>
    Task<ModelClassificationResult> ResetOverrideAsync(string modelName, CancellationToken cancellationToken = default);
}
