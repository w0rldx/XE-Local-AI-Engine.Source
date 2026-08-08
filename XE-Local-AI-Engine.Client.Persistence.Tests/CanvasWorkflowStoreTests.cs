namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class CanvasWorkflowStoreTests : IDisposable
{
    private const string GraphJson =
        "{\"nodes\":[{\"id\":\"start\",\"type\":\"start\",\"text\":\"SEED\"}," +
        "{\"id\":\"agent\",\"type\":\"agent\",\"instructions\":\"You are a careful planning agent.\"}]," +
        "\"edges\":[{\"from\":\"start\",\"to\":\"agent\"}]}";

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task CanvasWorkflowStore_AddThenGet_RoundTripsGraph()
    {
        var databasePath = GetDatabasePath("roundtrip.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid workflowId;
        await using (var writeContext = CreateContext(databasePath, keyHolder))
        {
            await writeContext.Database.EnsureDeletedAsync();
            await writeContext.Database.EnsureCreatedAsync();

            var store = new CanvasWorkflowStore(writeContext, TimeProvider.System);
            var added = await store.AddAsync(CreateInput());

            AssertEx.Equal("Workflow", added.Name);
            AssertEx.Equal(GraphJson, added.GraphJson);
            AssertEx.Equal(expected: 1, added.Version);
            AssertEx.True(added.Id != Guid.Empty, "Add should assign a workflow id.");
            AssertEx.True(added.CreatedAtUtc > 0, "Add should stamp a creation time.");
            AssertEx.Equal(added.CreatedAtUtc, added.UpdatedAtUtc);
            workflowId = added.Id;
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new CanvasWorkflowStore(readContext, TimeProvider.System);

        var byId = AssertEx.NotNull(await readStore.GetByIdAsync(workflowId), "Workflow should be found by id.");
        AssertEx.Equal("Workflow", byId.Name);
        AssertEx.Equal(GraphJson, byId.GraphJson);

        // ListAsync returns summaries only: name plaintext, but the graph blob is never loaded.
        var list = await readStore.ListAsync();
        AssertEx.Equal(expected: 1, list.Count);
        AssertEx.Equal("Workflow", list[0].Name);
        AssertEx.Null(list[0].GraphJson, "A list summary should omit the graph blob.");

        var unknown = await readStore.GetByIdAsync(Guid.NewGuid());
        AssertEx.Null(unknown, "Unknown id should return null.");
    }

    [Test]
    public async Task CanvasWorkflowStore_GraphJson_EncryptedAtRest()
    {
        var databasePath = GetDatabasePath("ciphertext.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var graph = "{\"secret\":\"SECRET-GRAPH-" + Guid.NewGuid().ToString("N") + "\"}";

        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var store = new CanvasWorkflowStore(context, TimeProvider.System);
            _ = await store.AddAsync(CreateInput() with
            {
                GraphJson = graph
            });
        }

        // Read the raw column bytes directly, bypassing the materialization interceptor, and assert the stored blob is
        // ciphertext: it must NOT decode back to the plaintext graph. This is load-bearing — it proves the SaveChanges
        // interceptor encrypted the blob (omitting that edit leaves plaintext instructions at rest).
        var rawBlob = await ReadRawGraphBlobAsync(databasePath);
        AssertEx.True(rawBlob.Length > 0, "The stored graph blob should not be empty.");

        var decoded = TryDecodeUtf8(rawBlob);
        AssertEx.True(decoded is null || !decoded.Contains("SECRET-GRAPH", StringComparison.Ordinal),
            "The raw graph blob must be ciphertext, not the UTF-8 plaintext graph.");

        // Also assert the plaintext bytes do not appear anywhere in the raw blob.
        AssertEx.False(ContainsSubsequence(rawBlob, Encoding.UTF8.GetBytes(graph)),
            "The raw graph blob should not contain the plaintext graph bytes.");
    }

    [Test]
    public async Task CanvasWorkflowStore_Update_StaleVersion_Conflicts()
    {
        var databasePath = GetDatabasePath("stale-version.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var clock = new MutableTimeProvider(1_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new CanvasWorkflowStore(context, clock);

        var added = await store.AddAsync(CreateInput());
        AssertEx.Equal(expected: 1, added.Version);

        // A correct expected version updates the row and bumps the version to 2.
        clock.Advance(10);
        var firstUpdate = await store.UpdateAsync(added.Id, expectedVersion: 1, CreateInput() with
        {
            Name = "Renamed",
            GraphJson = "{\"nodes\":[],\"edges\":[]}"
        });
        AssertEx.Equal(CanvasWorkflowUpdateOutcome.Updated, firstUpdate.Outcome);
        var updatedRecord = AssertEx.NotNull(firstUpdate.Record, "A successful update should return the record.");
        AssertEx.Equal(expected: 2, updatedRecord.Version);
        AssertEx.Equal("Renamed", updatedRecord.Name);
        AssertEx.True(updatedRecord.UpdatedAtUtc > added.UpdatedAtUtc, "A graph change should advance UpdatedAtUtc.");

        // Re-applying the now-stale expected version (1) must be rejected as a conflict without mutating the row.
        clock.Advance(10);
        var stale = await store.UpdateAsync(added.Id, expectedVersion: 1, CreateInput() with
        {
            Name = "Should not apply"
        });
        AssertEx.Equal(CanvasWorkflowUpdateOutcome.Conflict, stale.Outcome);
        AssertEx.Null(stale.Record, "A conflict should not return a record.");

        var current = AssertEx.NotNull(await store.GetByIdAsync(added.Id), "Workflow should still exist.");
        AssertEx.Equal(expected: 2, current.Version);
        AssertEx.Equal("Renamed", current.Name);

        // Updating an unknown id returns NotFound (distinct from Conflict).
        var missing = await store.UpdateAsync(Guid.NewGuid(), expectedVersion: 1, CreateInput());
        AssertEx.Equal(CanvasWorkflowUpdateOutcome.NotFound, missing.Outcome);
    }

    [Test]
    public async Task CanvasWorkflowStore_Delete_RemovesRow()
    {
        var databasePath = GetDatabasePath("delete.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new CanvasWorkflowStore(context, TimeProvider.System);

        var added = await store.AddAsync(CreateInput());

        AssertEx.True(await store.DeleteAsync(added.Id), "Delete should report a removed row.");
        AssertEx.Null(await store.GetByIdAsync(added.Id), "Deleted workflow should no longer be found.");
        AssertEx.False(await store.DeleteAsync(added.Id), "Deleting a missing id should report no removal.");
    }

    private static CanvasWorkflowInput CreateInput()
    {
        return new CanvasWorkflowInput("Workflow", GraphJson);
    }

    private static async Task<byte[]> ReadRawGraphBlobAsync(string databasePath)
    {
        // The test database holds exactly one workflow, so this reads that single row's encrypted column.
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT graph_json FROM canvas_workflows LIMIT 1;";
        return (byte[])(await read.ExecuteScalarAsync())!;
    }

    private static string? TryDecodeUtf8(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
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
