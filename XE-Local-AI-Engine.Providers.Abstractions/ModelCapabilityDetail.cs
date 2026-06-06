namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>Capability detail probed for a single model.</summary>
/// <param name="MaxContextTokens">Maximum context length the model advertises, when discoverable.</param>
public sealed record ModelCapabilityDetail(int? MaxContextTokens);
