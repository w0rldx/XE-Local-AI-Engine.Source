namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Provenance-path store tests for the starter-pack seed: <c>AddSeededAsync</c> stamps <c>Seeded</c> + the slug,
///     the normal <c>AddAsync</c> path stays <c>Manual</c> (forge-proof), seeded instructions encrypt at rest, the
///     slug projection is decrypt-free, and the filtered-unique index rejects a duplicate non-null slug.
/// </summary>
public sealed class AgentDefinitionSeededStoreTests : IDisposable
{
    private const string Instructions = "You are a seeded starter-pack agent. Follow the repository conventions exactly.";
    private const string Slug = "engineering-backend-architect";
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    [Test]
    public async Task AddSeededAsync_SetsSeededProvenanceAndSlug()
    {
        var databasePath = GetDatabasePath("seeded.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid definitionId;
        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var store = new AgentDefinitionStore(context, TimeProvider.System);

            var added = await store.AddSeededAsync(CreateInput(), Slug);

            AssertEx.Equal(AgentDefinitionSource.Seeded, added.Source);
            AssertEx.Equal(Slug, added.SeedSlug);
            definitionId = added.Id;
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new AgentDefinitionStore(readContext, TimeProvider.System);

        var record = AssertEx.NotNull(await readStore.GetByIdAsync(definitionId), "Seeded definition should be found by id.");
        AssertEx.Equal(AgentDefinitionSource.Seeded, record.Source);
        AssertEx.Equal(Slug, record.SeedSlug);
        AssertEx.Equal(Instructions, record.Instructions);

        var seededSlugs = await readStore.ListSeededSlugsAsync();
        AssertEx.True(seededSlugs.Contains(Slug), "The seeded slug should be reported by ListSeededSlugsAsync.");
    }

    [Test]
    public async Task AddAsync_StaysManualWithNoSlug_AndIsNotReportedAsSeeded()
    {
        // Forge-proof: the normal create path never sets Seeded, so a manual row can never appear in the seeded slug set.
        var databasePath = GetDatabasePath("manual.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid definitionId;
        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var store = new AgentDefinitionStore(context, TimeProvider.System);

            var added = await store.AddAsync(CreateInput());

            AssertEx.Equal(AgentDefinitionSource.Manual, added.Source);
            AssertEx.Null(added.SeedSlug, "A manual create should leave the seed slug null.");
            definitionId = added.Id;
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new AgentDefinitionStore(readContext, TimeProvider.System);

        var record = AssertEx.NotNull(await readStore.GetByIdAsync(definitionId), "Manual definition should be found by id.");
        AssertEx.Equal(AgentDefinitionSource.Manual, record.Source);
        AssertEx.Null(record.SeedSlug, "A manual row should round-trip a null seed slug.");

        var seededSlugs = await readStore.ListSeededSlugsAsync();
        AssertEx.Empty(seededSlugs, "A manual-only database should report no seeded slugs.");
    }

    [Test]
    public async Task AddSeededAsync_EncryptsInstructionsAtRest()
    {
        var databasePath = GetDatabasePath("seeded-ciphertext.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var instructions = "SEEDED-PROMPT-" + Guid.NewGuid().ToString("N");

        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var store = new AgentDefinitionStore(context, TimeProvider.System);
            _ = await store.AddSeededAsync(CreateInput() with
            {
                Instructions = instructions
            }, Slug);
        }

        var fileBytes = await File.ReadAllBytesAsync(databasePath);
        AssertEx.False(ContainsSubsequence(fileBytes, Encoding.UTF8.GetBytes(instructions)),
            "Seeded instructions must be encrypted at rest exactly like the manual-create path.");
    }

    [Test]
    public async Task AddSeededAsync_WhenSlugAlreadySeeded_FailsOnFilteredUniqueIndex()
    {
        // The DB-level filtered unique index is the defense-in-depth guard beneath the service-level skip: a second
        // insert of the same non-null slug must be rejected.
        var databasePath = GetDatabasePath("dup-slug.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentDefinitionStore(context, TimeProvider.System);

        _ = await store.AddSeededAsync(CreateInput(), Slug);

        _ = await AssertEx.ThrowsAsync<DbUpdateException>(() => store.AddSeededAsync(CreateInput(), Slug),
            "A second seeded insert of the same slug must violate the filtered unique index.");
    }

    private static AgentDefinitionInput CreateInput()
    {
        return new AgentDefinitionInput("Backend Architect",
            null,
            Instructions,
            null,
            null,
            AgentDefinitionKind.Single,
            [],
            new Dictionary<string, bool>(),
            null);
    }

    private static NodeChatDbContext CreateContext(string databasePath, INodeSqliteKeyHolder keyHolder)
    {
        return AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    private static byte[] CreateKeyMaterial()
    {
        return Enumerable.Range(0, 32).Select(static value => (byte)(value + 1)).ToArray();
    }

    private static bool ContainsSubsequence(byte[] source, byte[] needle)
    {
        if (needle.Length == 0)
        {
            return true;
        }

        for (var sourceIndex = 0; sourceIndex <= source.Length - needle.Length; sourceIndex++)
        {
            var matched = true;
            for (var needleIndex = 0; needleIndex < needle.Length; needleIndex++)
            {
                if (source[sourceIndex + needleIndex] == needle[needleIndex])
                {
                    continue;
                }

                matched = false;
                break;
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class FixedNodeSqliteKeyHolder(byte[] key) : INodeSqliteKeyHolder
    {
        private byte[]? _key = key;

        public ReadOnlyMemory<byte> Key
        {
            get
            {
                ObjectDisposedException.ThrowIf(_key is null, this);
                return _key;
            }
        }

        public void Dispose()
        {
            if (_key is null)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_key);
            _key = null;
        }
    }
}
