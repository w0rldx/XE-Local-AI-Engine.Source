namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Trusted worker-side projection of a persisted selected folder, carrying the resolved <see cref="HostPath" />.
/// </summary>
/// <remarks>
///     Decrypted on materialization, and for worker-internal use only — the model-facing surface never sees the host
///     path. See <c>SelectedFolderReference</c> in the application layer for the model-facing shape.
/// </remarks>
public sealed class SelectedFolderRecord
{
    public required Guid Id { get; init; }

    public required string Alias { get; init; }

    public required string HostPath { get; init; }

    public required SelectedFolderMode Mode { get; init; }

    public required long CreatedAtUtc { get; init; }

    public long? RevokedAtUtc { get; init; }
}
