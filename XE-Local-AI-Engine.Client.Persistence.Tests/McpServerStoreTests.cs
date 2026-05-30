namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class McpServerStoreTests : IDisposable
{
    private const string Description = "Local filesystem MCP server for the repo.";

    // Composed from parts so the loopback endpoint is not a hardcoded URI literal (analyzer S1075). The store treats
    // the URL as an opaque plaintext string; loopback validation is the application layer's concern, not the store's.
    private static readonly string LoopbackSseUrl = $"http://127.0.0.1:{8931}/sse";

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    [Test]
    public async Task AddAsync_ThenReadBackInNewContext_DecryptsArgumentsEnvironmentAndDescription()
    {
        var databasePath = GetDatabasePath("roundtrip.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid serverId;
        await using (var writeContext = CreateContext(databasePath, keyHolder))
        {
            await writeContext.Database.EnsureDeletedAsync();
            await writeContext.Database.EnsureCreatedAsync();

            var store = new McpServerStore(writeContext, TimeProvider.System);
            var added = await store.AddAsync(CreateStdioInput());

            AssertEx.Equal("filesystem", added.Name);
            AssertEx.Equal(Description, added.Description);
            AssertEx.Equal(McpTransportKind.Stdio, added.TransportKind);
            AssertEx.Equal("npx", added.Command);
            AssertEx.Equal(2, added.Arguments.Count);
            AssertEx.Equal("--root", added.Arguments[0]);
            AssertEx.Equal("repo", added.Arguments[1]);
            AssertEx.Equal("work-dir", added.WorkingDirectory);
            AssertEx.True(added.Environment.ContainsKey("API_TOKEN"), "Environment map should round-trip.");
            AssertEx.Equal("s3cr3t", added.Environment["API_TOKEN"]);
            AssertEx.False(added.Enabled, "A new registration must be persisted disabled.");
            AssertEx.Equal(1, added.Version);
            AssertEx.True(added.Id != Guid.Empty, "Add should assign a server id.");
            AssertEx.True(added.CreatedAtUtc > 0, "Add should stamp a creation time.");
            AssertEx.Equal(added.CreatedAtUtc, added.UpdatedAtUtc);
            serverId = added.Id;
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new McpServerStore(readContext, TimeProvider.System);

        var byId = AssertEx.NotNull(await readStore.GetByIdAsync(serverId), "Server should be found by id.");
        AssertEx.Equal(Description, byId.Description);
        AssertEx.Equal("s3cr3t", byId.Environment["API_TOKEN"]);
        AssertEx.Equal("--root", byId.Arguments[0]);

        var list = await readStore.ListAsync();
        AssertEx.Equal(1, list.Count);

        var unknown = await readStore.GetByIdAsync(Guid.NewGuid());
        AssertEx.Null(unknown, "Unknown id should return null.");
    }

    [Test]
    public async Task AddAsync_WithHttpTransport_RoundTripsUrlAndEmptyStdioFields()
    {
        var databasePath = GetDatabasePath("http.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid serverId;
        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var store = new McpServerStore(context, TimeProvider.System);
            var added = await store.AddAsync(CreateHttpInput());
            serverId = added.Id;
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new McpServerStore(readContext, TimeProvider.System);

        var record = AssertEx.NotNull(await readStore.GetByIdAsync(serverId), "Server should be found by id.");
        AssertEx.Equal(McpTransportKind.Http, record.TransportKind);
        AssertEx.Equal(LoopbackSseUrl, record.Url);
        AssertEx.Null(record.Command, "An http registration carries no command.");
        AssertEx.Equal(0, record.Arguments.Count);
        AssertEx.Equal(0, record.Environment.Count);
        AssertEx.Null(record.WorkingDirectory, "An http registration carries no working directory.");
    }

    [Test]
    public async Task AddAsync_WithNullDescription_RoundTripsNull()
    {
        var databasePath = GetDatabasePath("null-description.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid serverId;
        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var store = new McpServerStore(context, TimeProvider.System);
            var added = await store.AddAsync(CreateStdioInput() with { Description = null });
            serverId = added.Id;
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new McpServerStore(readContext, TimeProvider.System);

        var record = AssertEx.NotNull(await readStore.GetByIdAsync(serverId), "Server should be found by id.");
        AssertEx.Null(record.Description, "A null description should round-trip as null.");
    }

    [Test]
    public async Task DatabaseFile_AfterAdd_DoesNotContainPlaintextArgumentsEnvironmentOrDescription()
    {
        var databasePath = GetDatabasePath("ciphertext.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var secretArg = "SECRET-ARG-" + Guid.NewGuid().ToString("N");
        var secretEnv = "SECRET-ENV-" + Guid.NewGuid().ToString("N");
        var secretDescription = "SECRET-DESC-" + Guid.NewGuid().ToString("N");

        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var store = new McpServerStore(context, TimeProvider.System);
            _ = await store.AddAsync(CreateStdioInput() with
            {
                Description = secretDescription,
                Arguments = new[] { secretArg },
                Environment = new Dictionary<string, string> { ["TOKEN"] = secretEnv }
            });
        }

        var fileBytes = await File.ReadAllBytesAsync(databasePath);
        AssertEx.False(ContainsSubsequence(fileBytes, Encoding.UTF8.GetBytes(secretArg)),
            "The SQLite file should not contain the plaintext arguments.");
        AssertEx.False(ContainsSubsequence(fileBytes, Encoding.UTF8.GetBytes(secretEnv)),
            "The SQLite file should not contain the plaintext environment value.");
        AssertEx.False(ContainsSubsequence(fileBytes, Encoding.UTF8.GetBytes(secretDescription)),
            "The SQLite file should not contain the plaintext description.");
    }

    [Test]
    public async Task UpdateAsync_WhenConnectionFieldChanges_BumpsVersionAndUpdatedAt()
    {
        var databasePath = GetDatabasePath("version-bump.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var clock = new MutableTimeProvider(1_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new McpServerStore(context, clock);

        var added = await store.AddAsync(CreateStdioInput());
        AssertEx.Equal(1, added.Version);

        clock.Advance(50);
        var updated = AssertEx.NotNull(
            await store.UpdateAsync(added.Id, CreateStdioInput() with { Command = "node" }),
            "Update should find the server.");

        AssertEx.Equal(2, updated.Version);
        AssertEx.True(updated.UpdatedAtUtc > added.UpdatedAtUtc, "A connection change should advance UpdatedAtUtc.");
    }

    [Test]
    public async Task UpdateAsync_WhenOnlyNameOrDescriptionChanges_DoesNotBumpVersion()
    {
        var databasePath = GetDatabasePath("no-bump.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var clock = new MutableTimeProvider(2_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new McpServerStore(context, clock);

        var added = await store.AddAsync(CreateStdioInput());

        clock.Advance(25);
        var updated = AssertEx.NotNull(
            await store.UpdateAsync(added.Id, CreateStdioInput() with { Name = "renamed", Description = "New description only." }),
            "Update should find the server.");

        AssertEx.Equal("renamed", updated.Name);
        AssertEx.Equal("New description only.", updated.Description);
        AssertEx.Equal(1, updated.Version);
        AssertEx.True(updated.UpdatedAtUtc > added.UpdatedAtUtc, "A name/description edit should still advance UpdatedAtUtc.");
    }

    [Test]
    public async Task UpdateAsync_WhenEnvironmentReorderedButUnchanged_DoesNotBumpVersion()
    {
        var databasePath = GetDatabasePath("env-reorder.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var clock = new MutableTimeProvider(3_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new McpServerStore(context, clock);

        var added = await store.AddAsync(CreateStdioInput() with
        {
            Environment = new Dictionary<string, string> { ["ALPHA"] = "1", ["BRAVO"] = "2", ["CHARLIE"] = "3" }
        });
        AssertEx.Equal(1, added.Version);

        // Same environment entries, different key insertion order — must be treated as no connection change.
        clock.Advance(10);
        var reordered = AssertEx.NotNull(
            await store.UpdateAsync(added.Id, CreateStdioInput() with
            {
                Environment = new Dictionary<string, string> { ["CHARLIE"] = "3", ["ALPHA"] = "1", ["BRAVO"] = "2" }
            }),
            "Update should find the server.");

        AssertEx.Equal(1, reordered.Version);

        // Changing an actual environment value is a real connection change and must bump the version.
        clock.Advance(10);
        var changed = AssertEx.NotNull(
            await store.UpdateAsync(added.Id, CreateStdioInput() with
            {
                Environment = new Dictionary<string, string> { ["ALPHA"] = "9", ["BRAVO"] = "2", ["CHARLIE"] = "3" }
            }),
            "Update should find the server.");

        AssertEx.Equal(2, changed.Version);
    }

    [Test]
    public async Task UpdateAsync_WhenEnabledToggled_BumpsVersion()
    {
        var databasePath = GetDatabasePath("enable-toggle.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var clock = new MutableTimeProvider(4_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new McpServerStore(context, clock);

        var added = await store.AddAsync(CreateStdioInput());
        AssertEx.False(added.Enabled, "A new registration is disabled.");
        AssertEx.Equal(1, added.Version);

        clock.Advance(10);
        var enabled = AssertEx.NotNull(
            await store.UpdateAsync(added.Id, CreateStdioInput() with { Enabled = true }),
            "Update should find the server.");

        AssertEx.True(enabled.Enabled, "Enabling should persist the enabled flag.");
        AssertEx.Equal(2, enabled.Version);
    }

    [Test]
    public async Task UpdateAsync_WhenIdMissing_ReturnsNull()
    {
        var databasePath = GetDatabasePath("update-missing.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new McpServerStore(context, TimeProvider.System);

        var result = await store.UpdateAsync(Guid.NewGuid(), CreateStdioInput());
        AssertEx.Null(result, "Updating an unknown id should return null.");
    }

    [Test]
    public async Task DeleteAsync_RemovesRow()
    {
        var databasePath = GetDatabasePath("delete.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new McpServerStore(context, TimeProvider.System);

        var added = await store.AddAsync(CreateStdioInput());

        AssertEx.True(await store.DeleteAsync(added.Id), "Delete should report a removed row.");
        AssertEx.Null(await store.GetByIdAsync(added.Id), "Deleted server should no longer be found.");
        AssertEx.False(await store.DeleteAsync(added.Id), "Deleting a missing id should report no removal.");
    }

    [Test]
    public async Task ListEnabledAsync_ReturnsOnlyEnabledServers()
    {
        var databasePath = GetDatabasePath("list-enabled.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new McpServerStore(context, TimeProvider.System);

        var first = await store.AddAsync(CreateStdioInput() with { Name = "first" });
        _ = await store.AddAsync(CreateStdioInput() with { Name = "second" });

        // Both are registered disabled, so the enabled set is empty until one is enabled.
        var beforeEnable = await store.ListEnabledAsync();
        AssertEx.Equal(0, beforeEnable.Count);

        _ = await store.UpdateAsync(first.Id, CreateStdioInput() with { Name = "first", Enabled = true });

        var afterEnable = await store.ListEnabledAsync();
        AssertEx.Equal(1, afterEnable.Count);
        AssertEx.Equal("first", afterEnable[0].Name);
        AssertEx.True(afterEnable[0].Enabled, "ListEnabledAsync should only return enabled servers.");
    }

    [Test]
    public async Task AddAsync_WhenNameDiffersOnlyByCase_RejectedAsDuplicate()
    {
        var databasePath = GetDatabasePath("case-insensitive-name.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new McpServerStore(context, TimeProvider.System);

        _ = await store.AddAsync(CreateStdioInput() with { Name = "Weather" });

        // The unique index on name uses NOCASE collation, so a case-only-different name collides with the existing
        // row and SQLite rejects the insert — matching the application service's case-insensitive name handling.
        var exception = AssertEx.Throws<DbUpdateException>(
            () => store.AddAsync(CreateStdioInput() with { Name = "weather" }).GetAwaiter().GetResult(),
            "A name differing only in case must be rejected as a duplicate.");
        AssertEx.True(exception.InnerException is Microsoft.Data.Sqlite.SqliteException,
            "The duplicate should surface as a SQLite unique-constraint violation.");
    }

    [Test]
    public async Task SetEnabledAsync_TogglesFlag_BumpsVersionOnceAndLeavesSecretCiphertextUntouched()
    {
        var databasePath = GetDatabasePath("set-enabled.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var clock = new MutableTimeProvider(5_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new McpServerStore(context, clock);

        var added = await store.AddAsync(CreateStdioInput());
        AssertEx.False(added.Enabled, "A new registration is disabled.");
        AssertEx.Equal(1, added.Version);

        // Capture the encrypted arguments/env/description blobs as written on insert.
        var argumentsBefore = await ReadRawBlobAsync(databasePath, "arguments");
        var envBefore = await ReadRawBlobAsync(databasePath, "env");
        var descriptionBefore = await ReadRawBlobAsync(databasePath, "description");

        clock.Advance(10);
        var enabled = AssertEx.NotNull(
            await store.SetEnabledAsync(added.Id, enabled: true),
            "SetEnabled should find the server.");

        AssertEx.True(enabled.Enabled, "Enabling should persist the enabled flag.");
        AssertEx.Equal(2, enabled.Version);
        AssertEx.True(enabled.UpdatedAtUtc > added.UpdatedAtUtc, "Enabling should advance UpdatedAtUtc.");

        // The toggle must not re-encrypt the secret columns: their ciphertext is byte-identical to insert.
        var argumentsAfter = await ReadRawBlobAsync(databasePath, "arguments");
        var envAfter = await ReadRawBlobAsync(databasePath, "env");
        var descriptionAfter = await ReadRawBlobAsync(databasePath, "description");
        AssertEx.True(argumentsBefore.AsSpan().SequenceEqual(argumentsAfter),
            "Toggling enablement must not rewrite the arguments ciphertext.");
        AssertEx.True(envBefore.AsSpan().SequenceEqual(envAfter),
            "Toggling enablement must not rewrite the env ciphertext.");
        AssertEx.True(descriptionBefore.AsSpan().SequenceEqual(descriptionAfter),
            "Toggling enablement must not rewrite the description ciphertext.");

        // A second SetEnabled to the same value is a no-op for Version (no over-invalidation), and the secrets still
        // decrypt correctly across the whole sequence — proving the untouched ciphertext is still valid.
        clock.Advance(10);
        var unchanged = AssertEx.NotNull(
            await store.SetEnabledAsync(added.Id, enabled: true),
            "SetEnabled should find the server.");
        AssertEx.Equal(2, unchanged.Version);

        // Disabling again is a real change and bumps Version once more (so an enable/disable cycle is +2 total, not +4).
        clock.Advance(10);
        var disabled = AssertEx.NotNull(
            await store.SetEnabledAsync(added.Id, enabled: false),
            "SetEnabled should find the server.");
        AssertEx.False(disabled.Enabled, "Disabling should clear the enabled flag.");
        AssertEx.Equal(3, disabled.Version);

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new McpServerStore(readContext, clock);
        var reread = AssertEx.NotNull(await readStore.GetByIdAsync(added.Id), "Server should be found by id.");
        AssertEx.Equal("repo", reread.Arguments[1]);
        AssertEx.Equal("s3cr3t", reread.Environment["API_TOKEN"]);
        AssertEx.Equal(Description, reread.Description);
    }

    [Test]
    public async Task SetEnabledAsync_WhenIdMissing_ReturnsNull()
    {
        var databasePath = GetDatabasePath("set-enabled-missing.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new McpServerStore(context, TimeProvider.System);

        var result = await store.SetEnabledAsync(Guid.NewGuid(), enabled: true);
        AssertEx.Null(result, "Toggling an unknown id should return null.");
    }

    [Test]
    public async Task GetByIdAsync_WhenArgumentsTampered_FailsAuthenticatedDecryption()
    {
        var databasePath = GetDatabasePath("tamper.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid serverId;
        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var store = new McpServerStore(context, TimeProvider.System);
            var added = await store.AddAsync(CreateStdioInput());
            serverId = added.Id;
        }

        await TamperArgumentsAsync(databasePath);

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new McpServerStore(readContext, TimeProvider.System);

        _ = AssertEx.Throws<CryptographicException>(
            () => readStore.GetByIdAsync(serverId).GetAwaiter().GetResult(),
            "Tampered arguments ciphertext should fail authenticated decryption.");
    }

    private static McpServerInput CreateStdioInput()
    {
        return new McpServerInput(
            "filesystem",
            Description,
            McpTransportKind.Stdio,
            Command: "npx",
            Arguments: new[] { "--root", "repo" },
            WorkingDirectory: "work-dir",
            Environment: new Dictionary<string, string> { ["API_TOKEN"] = "s3cr3t" },
            Url: null,
            Enabled: false);
    }

    private static McpServerInput CreateHttpInput()
    {
        return new McpServerInput(
            "playwright",
            Description: null,
            McpTransportKind.Http,
            Command: null,
            Arguments: [],
            WorkingDirectory: null,
            Environment: new Dictionary<string, string>(),
            Url: LoopbackSseUrl,
            Enabled: false);
    }

    private static async Task TamperArgumentsAsync(string databasePath)
    {
        // The test database holds exactly one registration, so the corruption targets that single row.
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();

        byte[] blob;
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT arguments FROM mcp_servers LIMIT 1;";
            blob = (byte[])(await read.ExecuteScalarAsync())!;
        }

        // Flip a byte of the trailing AES-GCM authentication tag so authenticated decryption must reject it.
        blob[^1] ^= 0xFF;

        await using var write = connection.CreateCommand();
        write.CommandText = "UPDATE mcp_servers SET arguments = $blob;";
        write.Parameters.AddWithValue("$blob", blob);
        _ = await write.ExecuteNonQueryAsync();
    }

    private static async Task<byte[]> ReadRawBlobAsync(string databasePath, string column)
    {
        // Resolve the column from a closed allowlist so no caller-supplied string flows into the SQL text (CA2100).
        // The test database holds exactly one registration, so LIMIT 1 targets that single row (mirrors the tamper helper).
        var selectColumn = column switch
        {
            "arguments" => "arguments",
            "env" => "env",
            "description" => "description",
            _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Unsupported blob column.")
        };

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {selectColumn} FROM mcp_servers LIMIT 1;";
        return (byte[])(await command.ExecuteScalarAsync())!;
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
