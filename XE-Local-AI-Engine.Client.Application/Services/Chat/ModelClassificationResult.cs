namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Resolved classification for a single local model, projected for the list endpoint and the override actions.
///     <see cref="Kind" /> is the effective kind (<c>override ?? detected</c>, defaulting to
///     <see cref="ModelKind.Unknown" />); <see cref="DetectedKind" /> is the machine classification so the UI can show
///     a "reset to detected" affordance, and <see cref="Capabilities" /> are the raw capability strings for read-only
///     badges.
/// </summary>
public sealed record ModelClassificationResult(
    string ModelName,
    ModelKind Kind,
    ModelKind DetectedKind,
    IReadOnlyList<string> Capabilities,
    bool IsOverridden);
