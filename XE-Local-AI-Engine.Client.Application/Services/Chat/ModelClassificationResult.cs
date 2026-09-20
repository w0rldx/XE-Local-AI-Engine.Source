namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Resolved classification for a single local model, projected for the list endpoint and the override actions.
/// </summary>
/// <remarks>
///     <see cref="Kind" /> is the effective kind, <c>override ?? detected</c> defaulting to
///     <see cref="ModelKind.Unknown" />; <see cref="DetectedKind" /> is the machine classification, so the UI can
///     offer "reset to detected"; <see cref="Capabilities" /> are the raw strings for read-only badges.
/// </remarks>
public sealed class ModelClassificationResult
{
    public required string ModelName { get; init; }

    public required ModelKind Kind { get; init; }

    public required ModelKind DetectedKind { get; init; }

    public required IReadOnlyList<string> Capabilities { get; init; }

    public required bool IsOverridden { get; init; }
}
