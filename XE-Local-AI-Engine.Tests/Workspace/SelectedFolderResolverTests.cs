namespace XE_Local_AI_Engine.Tests.Workspace;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class SelectedFolderResolverTests
{
    // SelectedFolderResolver.IsSafeHostPath requires Path.IsPathFullyQualified, and "/trusted/..." is rooted but NOT
    // fully qualified on Windows — it has no drive, so it resolves against the current drive. A Unix literal therefore
    // fails registration before any of these tests reach their actual assertion. The production check is correct and
    // portable; only the fixture was Unix-only.
    private static readonly string TrustedHostPath = HostPath("trusted", "host", "projects", "repo-one");

    [Test]
    public async Task RegisterAsync_NormalizesAliasAndPersists()
    {
        var resolver = CreateResolver();

        var reference = await resolver.RegisterAsync(new SelectedFolderRegistration("Repo One!", TrustedHostPath));

        AssertEx.Equal("repo-one", reference.Alias);
        AssertEx.True(Guid.TryParse(reference.Id, out _), "The reference id should be a GUID string.");
    }

    [Test]
    public async Task RegisterAsync_WithRelativeHostPath_Throws()
    {
        var resolver = CreateResolver();

        var exception = await AssertEx.ThrowsAsync<SelectedFolderValidationException>(() => resolver.RegisterAsync(new SelectedFolderRegistration("repo-one", "relative/path")),
            "A relative host path should be rejected.");

        // A bad host path is an input problem, not a not-found or a conflict: the base type is what endpoints map to 400.
        AssertEx.Equal(typeof(SelectedFolderValidationException), exception.GetType());
    }

    [Test]
    public async Task RegisterAsync_WithPathTraversal_Throws()
    {
        var resolver = CreateResolver();

        // Still fully qualified on both platforms, so the '..' segment — not the qualification check — is what rejects it.
        var exception = await AssertEx.ThrowsAsync<SelectedFolderValidationException>(() => resolver.RegisterAsync(new SelectedFolderRegistration("repo-one", HostPath("trusted", "..", "etc", "passwd"))),
            "A traversal host path should be rejected.");

        AssertEx.Equal(typeof(SelectedFolderValidationException), exception.GetType());
    }

    [Test]
    public async Task RegisterAsync_WithUnusableAlias_Throws()
    {
        var resolver = CreateResolver();

        var exception = await AssertEx.ThrowsAsync<SelectedFolderValidationException>(() => resolver.RegisterAsync(new SelectedFolderRegistration("!!!", TrustedHostPath)),
            "An alias that normalizes to empty should be rejected.");

        AssertEx.Equal(typeof(SelectedFolderValidationException), exception.GetType());
    }

    [Test]
    public async Task RegisterAsync_WithDuplicateAlias_ThrowsConflict()
    {
        var resolver = CreateResolver();
        _ = await resolver.RegisterAsync(new SelectedFolderRegistration("repo-one", TrustedHostPath));

        _ = await AssertEx.ThrowsAsync<SelectedFolderConflictException>(() => resolver.RegisterAsync(new SelectedFolderRegistration("Repo-One", HostPath("trusted", "host", "other"))),
            "A colliding alias should be rejected after normalization, as a conflict rather than a plain input rejection.");
    }

    [Test]
    public async Task RegisterAsync_WhenStoreReportsUniqueViolation_ThrowsConflict()
    {
        var resolver = new SelectedFolderResolver(new ThrowingSelectedFolderStore(), NullLogger<SelectedFolderResolver>.Instance);

        _ = await AssertEx.ThrowsAsync<SelectedFolderConflictException>(() => resolver.RegisterAsync(new SelectedFolderRegistration("repo-one", TrustedHostPath)),
            "A unique-index violation surfacing from the store should be mapped to the same conflict as the pre-check.");
    }

    [Test]
    public async Task ResolveAsync_WithUnknownId_ThrowsNotFound()
    {
        var resolver = CreateResolver();

        _ = await AssertEx.ThrowsAsync<SelectedFolderNotFoundException>(() => resolver.ResolveAsync(Guid.NewGuid().ToString()),
            "A well-formed but unregistered id should be rejected as not-found, not as an input problem.");
    }

    [Test]
    public async Task ResolveAsync_WithNonGuidId_Throws()
    {
        var resolver = CreateResolver();

        var exception = await AssertEx.ThrowsAsync<SelectedFolderValidationException>(() => resolver.ResolveAsync("not-a-guid"),
            "A non-GUID id should be rejected.");

        // An unparsable id is malformed input (400), not a missing resource (404).
        AssertEx.Equal(typeof(SelectedFolderValidationException), exception.GetType());
    }

    [Test]
    public async Task ResolveAsync_WithKnownId_ReturnsTrustedHostPath()
    {
        var resolver = CreateResolver();
        var reference = await resolver.RegisterAsync(new SelectedFolderRegistration("repo-one", TrustedHostPath));

        var resolved = await resolver.ResolveAsync(reference.Id);

        AssertEx.Equal(TrustedHostPath, resolved.HostPath);
        AssertEx.Equal("repo-one", resolved.Alias);
        AssertEx.Equal(SelectedFolderMode.Copy, resolved.Mode);
    }

    [Test]
    public async Task ListReferencesAsync_ExposesIdAndAliasOnly()
    {
        var resolver = CreateResolver();
        _ = await resolver.RegisterAsync(new SelectedFolderRegistration("repo-one", TrustedHostPath));

        var references = await resolver.ListReferencesAsync();

        AssertEx.Equal(expected: 1, references.Count);
        AssertEx.Equal("repo-one", references[0].Alias);
        AssertEx.True(Guid.TryParse(references[0].Id, out _), "Listed references should carry a GUID id.");
    }

    /// <summary>
    ///     Builds a fully qualified host path for the running OS. Windows needs a drive to satisfy
    ///     <see cref="Path.IsPathFullyQualified" />; a bare leading slash is rooted but not qualified.
    /// </summary>
    private static string HostPath(params string[] segments) =>
        OperatingSystem.IsWindows()
            ? string.Concat(@"C:\", string.Join('\\', segments))
            : string.Concat("/", string.Join('/', segments));

    private static SelectedFolderResolver CreateResolver()
    {
        return new SelectedFolderResolver(new FakeSelectedFolderStore(), NullLogger<SelectedFolderResolver>.Instance);
    }

    private sealed class FakeSelectedFolderStore : INodeSelectedFolderStore
    {
        private readonly List<SelectedFolderRecord> _records = [];

        public Task<SelectedFolderRecord> AddAsync(string folderAlias, string hostPath, SelectedFolderMode mode, CancellationToken cancellationToken = default)
        {
            var record = new SelectedFolderRecord(Guid.NewGuid(), folderAlias, hostPath, mode, CreatedAtUtc: 1);
            _records.Add(record);
            return Task.FromResult(record);
        }

        public Task<SelectedFolderRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_records.FirstOrDefault(record => record.Id == id));
        }

        public Task<SelectedFolderRecord?> GetByAliasAsync(string folderAlias, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_records.FirstOrDefault(record => string.Equals(record.Alias, folderAlias, StringComparison.Ordinal)));
        }

        public Task<IReadOnlyList<SelectedFolderRecord>> ListAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SelectedFolderRecord>>(_records.ToArray());
        }

        public Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var removed = _records.RemoveAll(record => record.Id == id) == 1;
            return Task.FromResult(removed);
        }
    }

    private sealed class ThrowingSelectedFolderStore : INodeSelectedFolderStore
    {
        public Task<SelectedFolderRecord> AddAsync(string folderAlias, string hostPath, SelectedFolderMode mode, CancellationToken cancellationToken = default)
        {
            throw new DbUpdateException("SQLite Error 19: 'UNIQUE constraint failed: selected_folders.alias'.");
        }

        public Task<SelectedFolderRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SelectedFolderRecord?>(null);
        }

        public Task<SelectedFolderRecord?> GetByAliasAsync(string folderAlias, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SelectedFolderRecord?>(null);
        }

        public Task<IReadOnlyList<SelectedFolderRecord>> ListAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SelectedFolderRecord>>([]);
        }

        public Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }
    }
}
