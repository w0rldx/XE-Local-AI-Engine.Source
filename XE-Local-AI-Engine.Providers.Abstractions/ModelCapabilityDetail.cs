namespace XE_Local_AI_Engine.Providers.Abstractions;

public sealed class ModelCapabilityDetail
{
    /// <summary>Maximum context length the model advertises, when discoverable.</summary>
    public required int? MaxContextTokens { get; init; }
}
