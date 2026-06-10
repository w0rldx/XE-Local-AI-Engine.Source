namespace XE_Local_AI_Engine.Installer.Tests.Fakes;

using XE_Local_AI_Engine.Installer.State;

/// <summary>In-memory <see cref="IInstallStateStore" /> so the orchestrator is testable without disk.</summary>
internal sealed class InMemoryInstallStateStore : IInstallStateStore
{
    public InstallManifest? Manifest { get; private set; }

    public InstallState? State { get; private set; }

    public int ManifestWriteCount { get; private set; }

    public Task<InstallManifest?> ReadManifestAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Manifest);

    public Task WriteManifestAsync(InstallManifest manifest, CancellationToken cancellationToken = default)
    {
        Manifest = manifest;
        ManifestWriteCount++;
        return Task.CompletedTask;
    }

    public Task DeleteManifestAsync(CancellationToken cancellationToken = default)
    {
        Manifest = null;
        return Task.CompletedTask;
    }

    public Task<InstallState?> ReadStateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(State);

    public Task WriteStateAsync(InstallState state, CancellationToken cancellationToken = default)
    {
        State = state;
        return Task.CompletedTask;
    }

    public Task DeleteStateAsync(CancellationToken cancellationToken = default)
    {
        State = null;
        return Task.CompletedTask;
    }

    public void SeedManifest(InstallManifest manifest) => Manifest = manifest;

    public void SeedState(InstallState state) => State = state;
}
