namespace XE_Local_AI_Engine.Tests.Workspace;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
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

        var reference = await resolver.RegisterAsync(new SelectedFolderRegistration
        {
            Alias = "Repo One!",
            HostPath = TrustedHostPath
        });

        AssertEx.Equal("repo-one", reference.Alias);
        AssertEx.True(Guid.TryParse(reference.Id, out _), "The reference id should be a GUID string.");
    }

    [Test]
    public async Task RegisterAsync_WithRelativeHostPath_Throws()
    {
        var resolver = CreateResolver();

        var exception = await AssertEx.ThrowsAsync<SelectedFolderValidationException>(() => resolver.RegisterAsync(new SelectedFolderRegistration
            {
                Alias = "repo-one",
                HostPath = "relative/path"
            }),
            "A relative host path should be rejected.");

        // A bad host path is an input problem, not a not-found or a conflict: the base type is what endpoints map to 400.
        AssertEx.Equal(typeof(SelectedFolderValidationException), exception.GetType());
    }

    [Test]
    public async Task RegisterAsync_WithPathTraversal_Throws()
    {
        var resolver = CreateResolver();

        // Still fully qualified on both platforms, so the '..' segment — not the qualification check — is what rejects it.
        var exception = await AssertEx.ThrowsAsync<SelectedFolderValidationException>(() => resolver.RegisterAsync(new SelectedFolderRegistration
            {
                Alias = "repo-one",
                HostPath = HostPath("trusted", "..", "etc", "passwd")
            }),
            "A traversal host path should be rejected.");

        AssertEx.Equal(typeof(SelectedFolderValidationException), exception.GetType());
    }

    [Test]
    public async Task RegisterAsync_WithUnusableAlias_Throws()
    {
        var resolver = CreateResolver();

        var exception = await AssertEx.ThrowsAsync<SelectedFolderValidationException>(() => resolver.RegisterAsync(new SelectedFolderRegistration
            {
                Alias = "!!!",
                HostPath = TrustedHostPath
            }),
            "An alias that normalizes to empty should be rejected.");

        AssertEx.Equal(typeof(SelectedFolderValidationException), exception.GetType());
    }

    [Test]
    public async Task RegisterAsync_WithDuplicateAlias_ThrowsConflict()
    {
        var resolver = CreateResolver();
        _ = await resolver.RegisterAsync(new SelectedFolderRegistration
        {
            Alias = "repo-one",
            HostPath = TrustedHostPath
        });

        _ = await AssertEx.ThrowsAsync<SelectedFolderConflictException>(() => resolver.RegisterAsync(new SelectedFolderRegistration
            {
                Alias = "Repo-One",
                HostPath = HostPath("trusted", "host", "other")
            }),
            "A colliding alias should be rejected after normalization, as a conflict rather than a plain input rejection.");
    }

    [Test]
    public async Task RegisterAsync_WhenStoreReportsUniqueViolation_ThrowsConflict()
    {
        var resolver = new SelectedFolderResolver(new ThrowingSelectedFolderStore(), NullLogger<SelectedFolderResolver>.Instance);

        _ = await AssertEx.ThrowsAsync<SelectedFolderConflictException>(() => resolver.RegisterAsync(new SelectedFolderRegistration
            {
                Alias = "repo-one",
                HostPath = TrustedHostPath
            }),
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
    public async Task ResolveAsync_WithAValueThatIsNeitherGuidNorAlias_Throws()
    {
        var resolver = CreateResolver();

        // "not-a-guid" is NOT the right fixture for this any more: it is a well-formed ALIAS, so it now reaches the
        // alias lookup and comes back not-found. A value that can be neither is what still proves the input rejection.
        var exception = await AssertEx.ThrowsAsync<SelectedFolderValidationException>(() => resolver.ResolveAsync("Not A Guid!"),
            "A value that is neither a GUID nor a well-formed alias should be rejected.");

        // Malformed input (400), not a missing resource (404).
        AssertEx.Equal(typeof(SelectedFolderValidationException), exception.GetType());
    }

    [Test]
    public async Task ResolveAsync_WithKnownAlias_ReturnsTrustedHostPath()
    {
        // THE live-round regression (round 2, D2). run_in_agent_home's schema advertises "alias OR GUID" and Node
        // Settings only ever shows the operator an alias, but ResolveAsync used to Guid.TryParse and throw, so every
        // alias a model sent was rejected as "not a valid identifier" and only an opaque GUID — which no tool result
        // ever reveals to the model — could be made to work.
        var resolver = CreateResolver();
        var reference = await resolver.RegisterAsync(new SelectedFolderRegistration
        {
            Alias = "repo-one",
            HostPath = TrustedHostPath
        });

        var resolved = await resolver.ResolveAsync("repo-one");

        AssertEx.Equal(TrustedHostPath, resolved.HostPath);
        AssertEx.Equal("repo-one", resolved.Alias);
        AssertEx.Equal(SelectedFolderMode.Copy, resolved.Mode);
        AssertEx.Equal(reference.Id, resolved.Id.ToString());
    }

    [Test]
    public async Task ResolveAsync_WithUnknownAlias_ThrowsNotFound()
    {
        var resolver = CreateResolver();

        // NotFound derives from SelectedFolderValidationException, which is the type AgentHomeToolGateway catches and
        // renders as "run_in_agent_home rejected: …", so an unknown alias still reaches the model as a clear sentence.
        _ = await AssertEx.ThrowsAsync<SelectedFolderNotFoundException>(() => resolver.ResolveAsync("no-such-folder"),
            "A well-formed but unregistered alias should be not-found, not an input problem.");
    }

    [Test]
    public async Task ResolveAsync_WithUppercaseAlias_Throws()
    {
        // Registration normalizes, resolution does NOT: stored aliases are always canonical, so an exact match finds
        // exactly what exists. Normalizing here would let a caller passing unvalidated text resolve "Repo One" to
        // "repo-one", which is a quieter contract than this seam should have. The tool schema is lowercase-only too.
        var resolver = CreateResolver();
        _ = await resolver.RegisterAsync(new SelectedFolderRegistration
        {
            Alias = "repo-one",
            HostPath = TrustedHostPath
        });

        var exception = await AssertEx.ThrowsAsync<SelectedFolderValidationException>(() => resolver.ResolveAsync("Repo-One"),
            "An alias is matched exactly; an uppercase variant is not a well-formed alias.");

        AssertEx.Equal(typeof(SelectedFolderValidationException), exception.GetType());
    }

    [Test]
    public async Task ResolveAsync_WhenAnAliasLooksLikeAGuid_ThePlainIdWins_AndTheAliasStaysReachable()
    {
        // An alias of GUID shape IS registrable: NormalizeAlias leaves lowercase hex and hyphens alone and the alias
        // shape regex accepts the result. So the order is load-bearing — a real id must never be shadowed by an alias
        // that merely looks like one — and the fall-through is what keeps such an alias reachable rather than masked.
        var resolver = CreateResolver();
        var decoyAlias = Guid.NewGuid().ToString();
        var realFolder = await resolver.RegisterAsync(new SelectedFolderRegistration
        {
            Alias = "repo-one",
            HostPath = TrustedHostPath
        });
        var decoyFolder = await resolver.RegisterAsync(new SelectedFolderRegistration
        {
            Alias = decoyAlias,
            HostPath = HostPath("trusted", "host", "decoy")
        });

        // The decoy's alias is not any folder's id, so resolving it must still find the decoy by alias.
        var byDecoyAlias = await resolver.ResolveAsync(decoyAlias);
        AssertEx.Equal(decoyFolder.Id, byDecoyAlias.Id.ToString());

        // And a real id still resolves to its own folder, never to a same-shaped alias.
        var byRealId = await resolver.ResolveAsync(realFolder.Id);
        AssertEx.Equal("repo-one", byRealId.Alias);
    }

    [Test]
    public async Task ResolveAsync_AfterRevocation_RejectsBothTheIdAndTheAlias()
    {
        // Alias resolution must inherit every guard the id path has: the store hides a revoked folder from BOTH
        // lookups, so a removed folder cannot be reached by the friendlier handle.
        var store = new FakeSelectedFolderStore();
        var resolver = new SelectedFolderResolver(store, NullLogger<SelectedFolderResolver>.Instance);
        var reference = await resolver.RegisterAsync(new SelectedFolderRegistration
        {
            Alias = "repo-one",
            HostPath = TrustedHostPath
        });

        AssertEx.True(await store.RevokeAsync(Guid.Parse(reference.Id)), "the folder should have been revoked");

        _ = await AssertEx.ThrowsAsync<SelectedFolderNotFoundException>(() => resolver.ResolveAsync("repo-one"),
            "A revoked folder must not be reachable by alias.");
        _ = await AssertEx.ThrowsAsync<SelectedFolderNotFoundException>(() => resolver.ResolveAsync(reference.Id),
            "…nor by id.");
    }

    [Test]
    public async Task ResolveAsync_WithKnownId_ReturnsTrustedHostPath()
    {
        var resolver = CreateResolver();
        var reference = await resolver.RegisterAsync(new SelectedFolderRegistration
        {
            Alias = "repo-one",
            HostPath = TrustedHostPath
        });

        var resolved = await resolver.ResolveAsync(reference.Id);

        AssertEx.Equal(TrustedHostPath, resolved.HostPath);
        AssertEx.Equal("repo-one", resolved.Alias);
        AssertEx.Equal(SelectedFolderMode.Copy, resolved.Mode);
    }

    [Test]
    public async Task ListReferencesAsync_ExposesIdAndAliasOnly()
    {
        var resolver = CreateResolver();
        _ = await resolver.RegisterAsync(new SelectedFolderRegistration
        {
            Alias = "repo-one",
            HostPath = TrustedHostPath
        });

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
            var record = new SelectedFolderRecord
            {
                Id = Guid.NewGuid(),
                Alias = folderAlias,
                HostPath = hostPath,
                Mode = mode,
                CreatedAtUtc = 1
            };
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
