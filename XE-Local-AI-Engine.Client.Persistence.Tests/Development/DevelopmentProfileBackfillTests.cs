namespace XE_Local_AI_Engine.Client.Persistence.Tests.Development;

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Development;

/// <summary>
///     S1.7. Projects created before the command-profile column existed carry a null profile and cannot execute. The
///     backfill re-runs detection against the project's own repository — and, crucially, declines to guess when it
///     cannot see the repository at all.
/// </summary>
public sealed class DevelopmentProfileBackfillTests : IDisposable
{
    private readonly DevelopmentTestFixture _fixture = new();
    private readonly string _repositoryRoot = Path.Combine(Path.GetTempPath(), "xe-development-backfill-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _fixture.Dispose();
        if (Directory.Exists(_repositoryRoot))
        {
            Directory.Delete(_repositoryRoot, recursive: true);
        }
    }

    [Test]
    public async Task Backfill_DetectsTheSolutionProfileAndPersistsItWithABumpedConfigurationVersion()
    {
        Directory.CreateDirectory(_repositoryRoot);
        await File.WriteAllTextAsync(Path.Combine(_repositoryRoot, "Fixture.slnx"), "<Solution />").ConfigureAwait(false);

        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var seed = DevelopmentTestFixture.CreateSeed();
        _ = await store.CreateProjectAsync(seed).ConfigureAwait(false);

        var created = await store.GetProjectAsync(seed.ProjectId).ConfigureAwait(false);
        AssertEx.Null(created.CommandProfileJson);

        var backfill = CreateBackfill(store, new StubRepositoryBindings(_repositoryRoot));
        var filled = await backfill.EnsureAsync(created).ConfigureAwait(false);

        var profile = AssertEx.NotNull(DevelopmentProfileSummary.TryFrom(filled.CommandProfileJson));
        AssertEx.Equal(DevelopmentCommandProfileCatalog.DotnetSlnx, profile.ProfileId);
        AssertEx.Equal("Fixture.slnx", profile.BuildTarget);
        AssertEx.Equal(expected: 64, profile.Digest.Length);

        // The bump is the point of persisting through the store rather than writing the column directly: anything
        // tracking project configuration has to be able to see that the profile appeared.
        AssertEx.Equal(created.ConfigurationVersion + 1, filled.ConfigurationVersion);
        AssertEx.Equal(created.Version + 1, filled.Version);

        var reloaded = await store.GetProjectAsync(seed.ProjectId).ConfigureAwait(false);
        AssertEx.Equal(filled.CommandProfileJson, reloaded.CommandProfileJson);

        // The stored bytes must satisfy the strict execution-time gate, not merely parse.
        _ = DevelopmentCommandProfileCatalog.ResolveStored(reloaded.CommandProfileJson);
    }

    /// <summary>
    ///     The load-bearing negative. Substituting <c>generic-git</c> for a repository nobody could inspect would
    ///     downgrade a real .NET project's gate to the whitespace check while still reporting a green validation, so an
    ///     unreachable repository must leave the profile null and let the existing "re-register" error stand.
    /// </summary>
    [Test]
    public async Task Backfill_LeavesTheProfileNullWhenTheRepositoryCannotBeReached()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var seed = DevelopmentTestFixture.CreateSeed();
        _ = await store.CreateProjectAsync(seed).ConfigureAwait(false);
        var created = await store.GetProjectAsync(seed.ProjectId).ConfigureAwait(false);

        var backfill = CreateBackfill(store, new UnavailableRepositoryBindings());
        var result = await backfill.EnsureAsync(created).ConfigureAwait(false);

        AssertEx.Null(result.CommandProfileJson);
        AssertEx.Equal(created.Version, result.Version);
        AssertEx.Equal(created.ConfigurationVersion, result.ConfigurationVersion);

        var reloaded = await store.GetProjectAsync(seed.ProjectId).ConfigureAwait(false);
        AssertEx.Null(reloaded.CommandProfileJson);
        _ = AssertEx.Throws<DevelopmentWorkspaceSecurityException>(() => DevelopmentCommandProfileCatalog.ResolveStored(reloaded.CommandProfileJson));
    }

    [Test]
    public async Task Backfill_NeverReplacesAProfileTheOperatorAlreadyConfirmed()
    {
        Directory.CreateDirectory(_repositoryRoot);
        await File.WriteAllTextAsync(Path.Combine(_repositoryRoot, "Fixture.slnx"), "<Solution />").ConfigureAwait(false);

        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();

        var confirmed = Encoding.UTF8.GetString(DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null).ToCanonicalUtf8());
        var seed = DevelopmentTestFixture.CreateSeed() with
        {
            CommandProfileJson = confirmed
        };
        _ = await store.CreateProjectAsync(seed).ConfigureAwait(false);
        var created = await store.GetProjectAsync(seed.ProjectId).ConfigureAwait(false);

        // The repository would detect dotnet-slnx, so a backfill that overwrote would visibly change the profile.
        var backfill = CreateBackfill(store, new StubRepositoryBindings(_repositoryRoot));
        var result = await backfill.EnsureAsync(created).ConfigureAwait(false);

        AssertEx.Equal(confirmed, result.CommandProfileJson);
        AssertEx.Equal(created.Version, result.Version);
        AssertEx.Equal(DevelopmentCommandProfileCatalog.GenericGit,
            AssertEx.NotNull(DevelopmentProfileSummary.TryFrom(result.CommandProfileJson)).ProfileId);
    }

    [Test]
    public async Task BackfillAll_FillsOnlyTheProjectsThatAreMissingAProfile()
    {
        Directory.CreateDirectory(_repositoryRoot);
        await File.WriteAllTextAsync(Path.Combine(_repositoryRoot, "Fixture.slnx"), "<Solution />").ConfigureAwait(false);

        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();

        var legacy = DevelopmentTestFixture.CreateSeed();
        _ = await store.CreateProjectAsync(legacy).ConfigureAwait(false);
        var current = DevelopmentTestFixture.CreateSeed() with
        {
            CommandProfileJson = Encoding.UTF8.GetString(DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null).ToCanonicalUtf8())
        };
        _ = await store.CreateProjectAsync(current).ConfigureAwait(false);

        var backfill = CreateBackfill(store, new StubRepositoryBindings(_repositoryRoot));
        AssertEx.Equal(expected: 1, await backfill.BackfillAllAsync().ConfigureAwait(false));

        // A second pass has nothing left to do, which is what makes running this on every startup safe.
        AssertEx.Equal(expected: 0, await backfill.BackfillAllAsync().ConfigureAwait(false));

        AssertEx.Equal(DevelopmentCommandProfileCatalog.DotnetSlnx,
            AssertEx.NotNull(DevelopmentProfileSummary.TryFrom((await store.GetProjectAsync(legacy.ProjectId).ConfigureAwait(false)).CommandProfileJson)).ProfileId);
        AssertEx.Equal(DevelopmentCommandProfileCatalog.GenericGit,
            AssertEx.NotNull(DevelopmentProfileSummary.TryFrom((await store.GetProjectAsync(current.ProjectId).ConfigureAwait(false)).CommandProfileJson)).ProfileId);
    }

    private static DevelopmentProfileBackfillService CreateBackfill(IDevelopmentStore store,
        IDevelopmentRepositoryBindingService bindings) =>
        new(store, bindings, new DevelopmentCommandProfileDetector(), NullLogger<DevelopmentProfileBackfillService>.Instance);

    private sealed class StubRepositoryBindings(string repositoryRoot) : StubRepositoryBindingsBase
    {
        public override Task<DevelopmentRepositoryBinding> ResolveProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DevelopmentRepositoryBinding(projectId,
                DevelopmentTestFixture.SelectedFolderId,
                "fixture",
                repositoryRoot,
                "repository-hash"));
    }

    private sealed class UnavailableRepositoryBindings : StubRepositoryBindingsBase
    {
        public override Task<DevelopmentRepositoryBinding> ResolveProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            throw new DevelopmentWorkspaceSecurityException("The selected repository is unavailable.");
    }

    private abstract class StubRepositoryBindingsBase : IDevelopmentRepositoryBindingService
    {
        public abstract Task<DevelopmentRepositoryBinding> ResolveProjectAsync(Guid projectId, CancellationToken cancellationToken = default);

        public Task<DevelopmentRepositoryReference> RegisterAsync(string displayAlias, string hostPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DevelopmentRepositoryReference>> ListAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DevelopmentRepositoryBinding> ResolveFolderAsync(Guid selectedFolderId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DevelopmentRepositoryBinding> ResolveExecutionAsync(DevelopmentExecutionSnapshot snapshot, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DevelopmentProjectSnapshot> ReconnectAsync(Guid projectId,
            Guid selectedFolderId,
            long expectedVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
