namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Trusted worker-side projection of a persisted selected folder. Carries the resolved <see cref="HostPath" />
///     (decrypted on materialization) and is for worker-internal use only — the model-facing surface never sees the
///     host path. See <c>SelectedFolderReference</c> in the application layer for the model-facing shape.
/// </summary>
public sealed record SelectedFolderRecord(Guid Id, string Alias, string HostPath, SelectedFolderMode Mode, long CreatedAtUtc);
