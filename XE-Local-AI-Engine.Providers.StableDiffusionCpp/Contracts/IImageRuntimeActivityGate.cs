namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>The image runtime's own activity gate; a distinct DI key from the transcription runtime's.</summary>
public interface IImageRuntimeActivityGate : IRuntimeActivityGate;
