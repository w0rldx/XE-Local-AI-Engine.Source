namespace XE_Local_AI_Engine.Tests.Workspace;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class SelectedFolderResolverTests
{
    private const string TrustedHostPath = "/trusted/host/projects/repo-one";

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

        _ = await AssertEx.ThrowsAsync<SelectedFolderValidationException>(() => resolver.RegisterAsync(new SelectedFolderRegistration("repo-one", "relative/path")),
            "A relative host path should be rejected.");
    }

    [Test]
    public async Task RegisterAsync_WithPathTraversal_Throws()
    {
        var resolver = CreateResolver();

        _ = await AssertEx.ThrowsAsync<SelectedFolderValidationException>(() => resolver.RegisterAsync(new SelectedFolderRegistration("repo-one", "/trusted/../etc/passwd")),
            "A traversal host path should be rejected.");
    }

    [Test]
    public async Task RegisterAsync_WithUnusableAlias_Throws()
    {
        var resolver = CreateResolver();

        _ = await AssertEx.ThrowsAsync<SelectedFolderValidationException>(() => resolver.RegisterAsync(new SelectedFolderRegistration("!!!", TrustedHostPath)),
            "An alias that normalizes to empty should be rejected.");
    }

    [Test]
    public async Task RegisterAsync_WithDuplicateAlias_Throws()
    {
        var resolver = CreateResolver();
        _ = await resolver.RegisterAsync(new SelectedFolderRegistration("repo-one", TrustedHostPath));

        _ = await AssertEx.ThrowsAsync<SelectedFolderValidationException>(() => resolver.RegisterAsync(new SelectedFolderRegistration("Repo-One", "/trusted/host/other")),
            "A colliding alias should be rejected after normalization.");
    }

    [Test]
    public async Task RegisterAsync_WhenStoreReportsUniqueViolation_ThrowsValidationException()
    {
        var resolver = new SelectedFolderResolver(new ThrowingSelectedFolderStore(), NullLogger<SelectedFolderResolver>.Instance);

        _ = await AssertEx.ThrowsAsync<SelectedFolderValidationException>(() => resolver.RegisterAsync(new SelectedFolderRegistration("repo-one", TrustedHostPath)),
            "A unique-index violation surfacing from the store should be mapped to a validation exception.");
    }

    [Test]
    public async Task ResolveAsync_WithUnknownId_Throws()
    {
        var resolver = CreateResolver();

        _ = await AssertEx.ThrowsAsync<SelectedFolderValidationException>(() => resolver.ResolveAsync(Guid.NewGuid().ToString()),
            "An unknown id should be rejected.");
    }

    [Test]
    public async Task ResolveAsync_WithNonGuidId_Throws()
    {
        var resolver = CreateResolver();

        _ = await AssertEx.ThrowsAsync<SelectedFolderValidationException>(() => resolver.ResolveAsync("not-a-guid"),
            "A non-GUID id should be rejected.");
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

        AssertEx.Equal(1, references.Count);
        AssertEx.Equal("repo-one", references[0].Alias);
        AssertEx.True(Guid.TryParse(references[0].Id, out _), "Listed references should carry a GUID id.");
    }

    private static SelectedFolderResolver CreateResolver()
    {
        return new SelectedFolderResolver(new FakeSelectedFolderStore(), NullLogger<SelectedFolderResolver>.Instance);
    }

    private sealed class FakeSelectedFolderStore : INodeSelectedFolderStore
    {
        private readonly List<SelectedFolderRecord> _records = [];

        public Task<SelectedFolderRecord> AddAsync(string folderAlias, string hostPath, SelectedFolderMode mode, CancellationToken cancellationToken = default)
        {
            var record = new SelectedFolderRecord(Guid.NewGuid(), folderAlias, hostPath, mode, 1);
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
    }
}
