namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

/// <summary>Compatibility result for a final llama-server launch vector.</summary>
internal sealed class LlamaServerCapabilityDecision
{
    public required LlamaServerLaunchSpec Spec { get; init; }

    public required bool IsCompatible { get; init; }

    public required bool CanTrySafeFallback { get; init; }

    public required string? SanitizedError { get; init; }

    public required IReadOnlyList<string> OmittedOptions { get; init; }
}
