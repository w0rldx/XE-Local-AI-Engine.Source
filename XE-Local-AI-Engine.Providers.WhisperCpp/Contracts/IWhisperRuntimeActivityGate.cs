namespace XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>The transcription runtime's own activity gate; a distinct DI key from the image runtime's.</summary>
public interface IWhisperRuntimeActivityGate : IRuntimeActivityGate;
