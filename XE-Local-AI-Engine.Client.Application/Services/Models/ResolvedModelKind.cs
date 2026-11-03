namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>A local model's effective kind, the kind the offline rules detect, and whether an override decided.</summary>
public sealed class ResolvedModelKind
{
    public required ModelKind Kind { get; init; }

    public required ModelKind DetectedKind { get; init; }

    public required bool IsOverridden { get; init; }
}
