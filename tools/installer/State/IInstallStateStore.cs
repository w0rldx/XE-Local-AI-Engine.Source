namespace XE_Local_AI_Engine.Installer.State;

/// <summary>
///     Persistence seam for the two on-disk JSON files (plan §6.1). Abstracted so the state machine
///     can be unit-tested with an in-memory store (no real disk). The production implementation writes
///     under <c>%ProgramData%\XE-Local-AI-Engine\installer\</c> and writes the manifest atomically LAST.
/// </summary>
public interface IInstallStateStore
{
    Task<InstallManifest?> ReadManifestAsync(CancellationToken cancellationToken = default);

    /// <summary>Atomic write — the manifest is the last install step (plan invariant 4).</summary>
    Task WriteManifestAsync(InstallManifest manifest, CancellationToken cancellationToken = default);

    Task DeleteManifestAsync(CancellationToken cancellationToken = default);

    Task<InstallState?> ReadStateAsync(CancellationToken cancellationToken = default);

    Task WriteStateAsync(InstallState state, CancellationToken cancellationToken = default);

    Task DeleteStateAsync(CancellationToken cancellationToken = default);
}
