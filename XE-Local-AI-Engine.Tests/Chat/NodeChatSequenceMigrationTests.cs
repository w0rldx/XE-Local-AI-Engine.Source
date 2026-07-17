namespace XE_Local_AI_Engine.Tests.Chat;

using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies the RepairAndUniqueMessageSequence migration (HIGH-003): a legacy database carrying duplicate
///     (conversation_id, sequence) rows from the pre-lock race is deterministically renumbered before the unique index
///     is created, and the index then enforces uniqueness. Also asserts the migration is a no-op on a clean database.
/// </summary>
public sealed class NodeChatSequenceMigrationTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task RepairMigration_RenumbersSeededDuplicatesThenEnforcesUniqueness()
    {
        await using var provider = BuildProvider("repair-duplicates.sqlite");
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

        // Target the repair migration by name (not by position): later migrations may be added after it, so it is not
        // necessarily the last one. "previous" is whatever migration immediately precedes it in the ordered list.
        var migrations = dbContext.Database.GetMigrations().ToList();
        var repairIndex = migrations.FindIndex(id => id.EndsWith("RepairAndUniqueMessageSequence", StringComparison.Ordinal));
        AssertEx.True(repairIndex > 0, "The RepairAndUniqueMessageSequence migration must exist and have a predecessor.");
        var repairMigration = migrations[repairIndex];
        var previousMigration = migrations[repairIndex - 1];
        var migrator = dbContext.GetInfrastructure().GetRequiredService<IMigrator>();

        // Apply every migration up to (but not including) the repair — the schema has the messages table WITHOUT the
        // unique index, so duplicate sequences can be seeded exactly as the pre-lock race produced them.
        await migrator.MigrateAsync(previousMigration).ConfigureAwait(false);

        var conversationId = Guid.NewGuid();
        await InsertRawMessageAsync(dbContext, conversationId, Guid.NewGuid(), sequence: 5, createdAtUtc: 100).ConfigureAwait(false);
        await InsertRawMessageAsync(dbContext, conversationId, Guid.NewGuid(), sequence: 5, createdAtUtc: 101).ConfigureAwait(false);
        await InsertRawMessageAsync(dbContext, conversationId, Guid.NewGuid(), sequence: 6, createdAtUtc: 102).ConfigureAwait(false);

        // Apply the repair migration: renumber duplicates, then create the unique index.
        await migrator.MigrateAsync(repairMigration).ConfigureAwait(false);

        var sequences = await ReadSequencesAsync(dbContext, conversationId).ConfigureAwait(false);
        AssertEx.Equal(expected: 3, sequences.Count);
        AssertEx.Equal(expected: 3, sequences.Distinct().Count());
        for (var expected = 0; expected < sequences.Count; expected++)
        {
            AssertEx.Equal(expected, sequences[expected], "Repaired sequences must be contiguous and gap-free from zero.");
        }

        // The unique index is now in force: another duplicate insert must be rejected by the database.
        var conflict = await AssertEx.ThrowsAsync<SqliteException>(() => InsertRawMessageAsync(dbContext, conversationId, Guid.NewGuid(), sequence: 0, createdAtUtc: 200))
                                     .ConfigureAwait(false);
        AssertEx.Equal(expected: 2067, conflict.SqliteExtendedErrorCode);
    }

    [Test]
    public async Task RepairMigration_IsANoOpOnACleanDatabase()
    {
        await using var provider = BuildProvider("repair-clean.sqlite");
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

        // Applying every migration (including the repair) against an empty database must succeed and leave the unique
        // index enforcing sequence uniqueness.
        await dbContext.Database.MigrateAsync().ConfigureAwait(false);

        var conversationId = Guid.NewGuid();
        await InsertRawMessageAsync(dbContext, conversationId, Guid.NewGuid(), sequence: 0, createdAtUtc: 1).ConfigureAwait(false);
        var conflict = await AssertEx.ThrowsAsync<SqliteException>(() => InsertRawMessageAsync(dbContext, conversationId, Guid.NewGuid(), sequence: 0, createdAtUtc: 2))
                                     .ConfigureAwait(false);
        AssertEx.Equal(expected: 2067, conflict.SqliteExtendedErrorCode);
    }

    private ServiceProvider BuildProvider(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, fileName);
        var services = new ServiceCollection();
        services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));

        return services.BuildServiceProvider(true);
    }

    private static async Task InsertRawMessageAsync(NodeChatDbContext dbContext, Guid conversationId, Guid messageId, int sequence, long createdAtUtc)
    {
        var connection = dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync().ConfigureAwait(false);
        }

        // The node runs with foreign-key enforcement OFF (no PRAGMA foreign_keys=ON on its connection); EF's provider
        // turns it on by default in tests, so disable it here to seed orphan message rows exactly like production.
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            await pragma.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO messages (message_id, conversation_id, sequence, role, content, created_at_utc, updated_at_utc, status, origin)
                              VALUES ($message_id, $conversation_id, $sequence, 'assistant', $content, $created_at_utc, $created_at_utc, 'completed', 'Local');
                              """;
        AddParameter(command, "$message_id", messageId.ToString());
        AddParameter(command, "$conversation_id", conversationId.ToString());
        AddParameter(command, "$sequence", sequence);
        AddParameter(command, "$content", new byte[]
        {
            1,
            2,
            3
        });
        AddParameter(command, "$created_at_utc", createdAtUtc);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<int>> ReadSequencesAsync(NodeChatDbContext dbContext, Guid conversationId)
    {
        var connection = dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync().ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sequence FROM messages WHERE conversation_id = $conversation_id ORDER BY sequence;";
        AddParameter(command, "$conversation_id", conversationId.ToString());
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var sequences = new List<int>();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            sequences.Add(reader.GetInt32(0));
        }

        return sequences;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
