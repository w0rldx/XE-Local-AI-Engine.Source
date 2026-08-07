namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class CustomToolStoreTests : IDisposable
{
    private const string Name = "custom__weather_lookup";
    private const string Description = "Fetch the current weather for a city.";
    private const string ParametersJson = """[{"name":"city","type":"string","description":"City name","required":true}]""";
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task CreateAsync_RoundTripsAllFields()
    {
        var databasePath = GetDatabasePath("roundtrip.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var configJson = """{"method":"GET","urlTemplate":"https://example.com/weather?q={city}","headers":[],"allowedHosts":["example.com"]}""";

        Guid toolId;
        await using (var writeContext = CreateContext(databasePath, keyHolder))
        {
            await writeContext.Database.EnsureDeletedAsync();
            await writeContext.Database.EnsureCreatedAsync();

            var store = new CustomToolStore(writeContext, TimeProvider.System);
            var added = await store.CreateAsync(new CustomToolInput(Name,
                Description,
                CustomToolKind.HttpFetch,
                CustomToolMode.Parameterized,
                configJson,
                ParametersJson,
                Acknowledged: true));

            AssertEx.Equal(Name, added.Name);
            AssertEx.Equal(Description, added.Description);
            AssertEx.Equal(CustomToolKind.HttpFetch, added.Kind);
            AssertEx.Equal(CustomToolMode.Parameterized, added.Mode);
            AssertEx.Equal(ParametersJson, added.ParametersJson);
            AssertEx.Equal(configJson, added.ConfigJson);
            AssertEx.True(added.Enabled, "A new tool should default to enabled.");
            AssertEx.True(added.Acknowledged, "The danger acknowledgement should round-trip.");
            AssertEx.Equal(expected: 1, added.Version);
            AssertEx.True(added.Id != Guid.Empty, "Create should assign a tool id.");
            AssertEx.True(added.CreatedAtUtc > 0, "Create should stamp a creation time.");
            AssertEx.Equal(added.CreatedAtUtc, added.UpdatedAtUtc);
            toolId = added.Id;
        }

        // Read back in a fresh context so the materialization interceptor decrypts from disk, not from the tracked
        // in-memory plaintext.
        await using (var readContext = CreateContext(databasePath, keyHolder))
        {
            var readStore = new CustomToolStore(readContext, TimeProvider.System);

            var byId = AssertEx.NotNull(await readStore.GetByIdAsync(toolId), "Tool should be found by id.");
            AssertEx.Equal(Description, byId.Description);
            AssertEx.Equal(configJson, byId.ConfigJson);
            AssertEx.Equal(ParametersJson, byId.ParametersJson);
            AssertEx.Equal(CustomToolKind.HttpFetch, byId.Kind);
            AssertEx.Equal(CustomToolMode.Parameterized, byId.Mode);

            var list = await readStore.ListAsync();
            AssertEx.Equal(expected: 1, list.Count);

            var unknown = await readStore.GetByIdAsync(Guid.NewGuid());
            AssertEx.Null(unknown, "Unknown id should return null.");
        }
    }

    [Test]
    public async Task SecretConfigValue_IsCiphertextAtRest()
    {
        var databasePath = GetDatabasePath("secret-config.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var secretValue = "SECRET-TOKEN-" + Guid.NewGuid().ToString("N");
        // A Command tool with a secret-flagged env value — exactly the shape whose plaintext must never touch disk.
        var configJson = $$"""{"executable":"/usr/bin/curl","argsTemplate":["--silent"],"env":[{"name":"API_TOKEN","value":"{{secretValue}}","isSecret":true}]}""";

        Guid toolId;
        await using (var writeContext = CreateContext(databasePath, keyHolder))
        {
            await writeContext.Database.EnsureDeletedAsync();
            await writeContext.Database.EnsureCreatedAsync();

            var store = new CustomToolStore(writeContext, TimeProvider.System);
            var added = await store.CreateAsync(new CustomToolInput(Name,
                Description,
                CustomToolKind.Command,
                CustomToolMode.Fixed,
                configJson,
                Acknowledged: true));
            toolId = added.Id;
        }

        // The decrypted read side still hands back the secret verbatim (masking is the CRUD read path's job, not the
        // store's) — proving the value survived encryption + decryption intact.
        await using (var readContext = CreateContext(databasePath, keyHolder))
        {
            var readStore = new CustomToolStore(readContext, TimeProvider.System);
            var reloaded = AssertEx.NotNull(await readStore.GetByIdAsync(toolId), "Tool should be found.");
            AssertEx.Equal(configJson, reloaded.ConfigJson);
        }

        // The raw SQLite file must never carry the plaintext secret or the surrounding config — only ciphertext at rest.
        var fileBytes = await SqliteFileProbe.ReadAllBytesAsync(databasePath);
        AssertEx.False(ContainsSubsequence(fileBytes, Encoding.UTF8.GetBytes(secretValue)),
            "The SQLite file should not contain the plaintext secret env value.");
        AssertEx.False(ContainsSubsequence(fileBytes, Encoding.UTF8.GetBytes(Description)),
            "The SQLite file should not contain the plaintext tool description.");
    }

    [Test]
    public async Task UpdateAsync_BumpsVersionOnConfigChange_NotOnEnabledOrAckToggle()
    {
        var databasePath = GetDatabasePath("version-bump.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var clock = new MutableTimeProvider(1_000);
        var configJson = """{"method":"GET","urlTemplate":"https://example.com/a"}""";

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new CustomToolStore(context, clock);

        var added = await store.CreateAsync(new CustomToolInput(Name, Description, CustomToolKind.HttpFetch, CustomToolMode.Fixed, configJson));
        AssertEx.Equal(expected: 1, added.Version);

        // Toggling Enabled and Acknowledged only gates the offered set / authoring; neither is model-facing content, so
        // neither may bump Version.
        clock.Advance(10);
        var toggled = AssertEx.NotNull(
            await store.UpdateAsync(added.Id, new CustomToolInput(Name, Description, CustomToolKind.HttpFetch, CustomToolMode.Fixed, configJson, Enabled: false, Acknowledged: true)),
            "Update should find the tool.");
        AssertEx.False(toggled.Enabled, "The disable toggle should round-trip.");
        AssertEx.True(toggled.Acknowledged, "The acknowledgement toggle should round-trip.");
        AssertEx.Equal(expected: 1, toggled.Version);
        AssertEx.True(toggled.UpdatedAtUtc > added.UpdatedAtUtc, "A toggle should still advance UpdatedAtUtc.");

        // Editing the config is content-affecting and must bump Version.
        clock.Advance(10);
        var editedConfig = """{"method":"GET","urlTemplate":"https://example.com/b"}""";
        var edited = AssertEx.NotNull(
            await store.UpdateAsync(added.Id, new CustomToolInput(Name, Description, CustomToolKind.HttpFetch, CustomToolMode.Fixed, editedConfig, Enabled: false, Acknowledged: true)),
            "Update should find the tool.");
        AssertEx.Equal(expected: 2, edited.Version);

        // Switching the mode is also content-affecting (it changes the approval floor and the model-facing schema).
        clock.Advance(10);
        var reMode = AssertEx.NotNull(
            await store.UpdateAsync(added.Id, new CustomToolInput(Name, Description, CustomToolKind.HttpFetch, CustomToolMode.Parameterized, editedConfig, Enabled: false, Acknowledged: true)),
            "Update should find the tool.");
        AssertEx.Equal(expected: 3, reMode.Version);
    }

    [Test]
    public async Task Name_IsNocaseUnique()
    {
        var databasePath = GetDatabasePath("nocase-unique.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var configJson = """{"method":"GET","urlTemplate":"https://example.com"}""";

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new CustomToolStore(context, TimeProvider.System);

        _ = await store.CreateAsync(new CustomToolInput("custom__weather", Description, CustomToolKind.HttpFetch, CustomToolMode.Fixed, configJson));

        var exception = AssertEx.Throws<DbUpdateException>(
            () => store.CreateAsync(new CustomToolInput("custom__WEATHER", Description, CustomToolKind.HttpFetch, CustomToolMode.Fixed, configJson)).GetAwaiter().GetResult(),
            "A name differing only in case must be rejected as a duplicate.");
        AssertEx.True(exception.InnerException is SqliteException,
            "The duplicate should surface as a SQLite unique-constraint violation.");
    }

    [Test]
    public async Task DeleteAsync_RemovesRow()
    {
        var databasePath = GetDatabasePath("delete.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var configJson = """{"method":"GET","urlTemplate":"https://example.com"}""";

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new CustomToolStore(context, TimeProvider.System);

        var added = await store.CreateAsync(new CustomToolInput(Name, Description, CustomToolKind.HttpFetch, CustomToolMode.Fixed, configJson));

        AssertEx.True(await store.DeleteAsync(added.Id), "Delete should report a removed row.");
        AssertEx.Null(await store.GetByIdAsync(added.Id), "Deleted tool should no longer be found.");
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
        return Enumerable.Range(start: 0, count: 32).Select(static value => (byte)(value + 1)).ToArray();
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
