namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AgentSkillStoreTests : IDisposable
{
    private const string Name = "code-review";
    private const string Description = "Review a diff for correctness and conventions.";
    private const string Body = "# Code review\n\nWalk the diff hunk by hunk. Flag bugs, then style.";
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    [Test]
    public async Task CreateAsync_RoundTripsAndEncryptsBlobs()
    {
        var databasePath = GetDatabasePath("roundtrip.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var description = "SECRET-DESC-" + Guid.NewGuid().ToString("N");
        var body = "SECRET-BODY-" + Guid.NewGuid().ToString("N");

        Guid skillId;
        await using (var writeContext = CreateContext(databasePath, keyHolder))
        {
            await writeContext.Database.EnsureDeletedAsync();
            await writeContext.Database.EnsureCreatedAsync();

            var store = new AgentSkillStore(writeContext, TimeProvider.System);
            var added = await store.CreateAsync(new AgentSkillInput(Name, description, body));

            AssertEx.Equal(Name, added.Name);
            AssertEx.Equal(description, added.Description);
            AssertEx.Equal(body, added.Body);
            AssertEx.True(added.Enabled, "A new skill should default to enabled.");
            AssertEx.Equal(1, added.Version);
            AssertEx.True(added.Id != Guid.Empty, "Create should assign a skill id.");
            AssertEx.True(added.CreatedAtUtc > 0, "Create should stamp a creation time.");
            AssertEx.Equal(added.CreatedAtUtc, added.UpdatedAtUtc);
            skillId = added.Id;
        }

        // Read back in a fresh context so the materialization interceptor decrypts from disk, not from the tracked
        // in-memory plaintext.
        await using (var readContext = CreateContext(databasePath, keyHolder))
        {
            var readStore = new AgentSkillStore(readContext, TimeProvider.System);

            var byId = AssertEx.NotNull(await readStore.GetByIdAsync(skillId), "Skill should be found by id.");
            AssertEx.Equal(description, byId.Description);
            AssertEx.Equal(body, byId.Body);

            var list = await readStore.ListAsync();
            AssertEx.Equal(1, list.Count);

            var unknown = await readStore.GetByIdAsync(Guid.NewGuid());
            AssertEx.Null(unknown, "Unknown id should return null.");
        }

        // The raw SQLite file must never carry the plaintext description or body — only ciphertext at rest.
        var fileBytes = await File.ReadAllBytesAsync(databasePath);
        AssertEx.False(ContainsSubsequence(fileBytes, Encoding.UTF8.GetBytes(description)),
            "The SQLite file should not contain the plaintext skill description.");
        AssertEx.False(ContainsSubsequence(fileBytes, Encoding.UTF8.GetBytes(body)),
            "The SQLite file should not contain the plaintext skill body.");
    }

    [Test]
    public async Task UpdateAsync_BumpsVersionOnBodyChange_NotOnEnabledToggle()
    {
        var databasePath = GetDatabasePath("version-bump.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var clock = new MutableTimeProvider(1_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentSkillStore(context, clock);

        var added = await store.CreateAsync(new AgentSkillInput(Name, Description, Body));
        AssertEx.Equal(1, added.Version);

        // Toggling Enabled alone gates resolution only; it must not bump Version (membership in the resolved set already
        // covers it in the config hash).
        clock.Advance(10);
        var toggled = AssertEx.NotNull(await store.UpdateAsync(added.Id, new AgentSkillInput(Name, Description, Body, false)),
            "Update should find the skill.");
        AssertEx.False(toggled.Enabled, "The disable toggle should round-trip.");
        AssertEx.Equal(1, toggled.Version);
        AssertEx.True(toggled.UpdatedAtUtc > added.UpdatedAtUtc, "An enabled toggle should still advance UpdatedAtUtc.");

        // Editing the body is content-affecting and must bump Version.
        clock.Advance(10);
        var edited = AssertEx.NotNull(await store.UpdateAsync(added.Id, new AgentSkillInput(Name, Description, "A different body.", false)),
            "Update should find the skill.");
        AssertEx.Equal(2, edited.Version);

        // A rename is also content-affecting (the model sees the name) and must bump Version.
        clock.Advance(10);
        var renamed = AssertEx.NotNull(await store.UpdateAsync(added.Id, new AgentSkillInput("renamed-skill", Description, "A different body.", false)),
            "Update should find the skill.");
        AssertEx.Equal(3, renamed.Version);
    }

    [Test]
    public async Task Name_IsNocaseUnique()
    {
        var databasePath = GetDatabasePath("nocase-unique.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentSkillStore(context, TimeProvider.System);

        _ = await store.CreateAsync(new AgentSkillInput("Weather", Description, Body));

        // The unique index on name uses NOCASE collation, so a case-only-different name collides with the existing row
        // and SQLite rejects the insert — matching the application service's case-insensitive name handling.
        var exception = AssertEx.Throws<DbUpdateException>(() => store.CreateAsync(new AgentSkillInput("weather", Description, Body)).GetAwaiter().GetResult(),
            "A name differing only in case must be rejected as a duplicate.");
        AssertEx.True(exception.InnerException is SqliteException,
            "The duplicate should surface as a SQLite unique-constraint violation.");
    }

    [Test]
    public async Task ListEnabledByIdsAsync_ReturnsOnlyEnabledAssignedSkills()
    {
        var databasePath = GetDatabasePath("list-enabled.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentSkillStore(context, TimeProvider.System);

        var enabled = await store.CreateAsync(new AgentSkillInput("alpha", Description, Body));
        var disabled = await store.CreateAsync(new AgentSkillInput("bravo", Description, Body, false));
        var unassigned = await store.CreateAsync(new AgentSkillInput("charlie", Description, Body));

        var resolved = await store.ListEnabledByIdsAsync(new[]
        {
            enabled.Id,
            disabled.Id,
            Guid.NewGuid()
        });

        // Only the enabled, assigned skill comes back: the disabled id is filtered server-side and the unknown id and
        // the unassigned skill are simply absent.
        AssertEx.Equal(1, resolved.Count);
        AssertEx.Equal(enabled.Id, resolved[0].Id);
        AssertEx.Equal(Description, resolved[0].Description);
        AssertEx.Equal(Body, resolved[0].Body);
        AssertEx.False(resolved.Any(skill => skill.Id == disabled.Id), "A disabled skill must never be resolved.");
        AssertEx.False(resolved.Any(skill => skill.Id == unassigned.Id), "An unassigned skill must never be resolved.");

        // An empty request short-circuits to an empty set without a query.
        var empty = await store.ListEnabledByIdsAsync([]);
        AssertEx.Empty(empty, "An empty id set should resolve to no skills.");
    }

    [Test]
    public async Task DeleteAsync_RemovesRow()
    {
        var databasePath = GetDatabasePath("delete.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentSkillStore(context, TimeProvider.System);

        var added = await store.CreateAsync(new AgentSkillInput(Name, Description, Body));

        AssertEx.True(await store.DeleteAsync(added.Id), "Delete should report a removed row.");
        AssertEx.Null(await store.GetByIdAsync(added.Id), "Deleted skill should no longer be found.");
        AssertEx.False(await store.DeleteAsync(added.Id), "Deleting a missing id should report no removal.");
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

    private sealed class MutableTimeProvider(long initialMilliseconds) : TimeProvider
    {
        private long _milliseconds = initialMilliseconds;

        public void Advance(long milliseconds)
        {
            _milliseconds += milliseconds;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(_milliseconds);
        }
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
