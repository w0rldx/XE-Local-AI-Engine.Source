namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class NodeConversationPinArchiveMigrationTests : IDisposable
{
    private const string PrePinArchiveMigrationId = "20260526115619_AddNodeChatOrigin";
    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }

        _keyHolder.Dispose();
    }

    [Test]
    public async Task MigrateAsync_WhenExistingConversationPresent_AddsPinArchiveColumnsDefaultingFalse()
    {
        var databasePath = GetDatabasePath("pin-archive-defaults.sqlite");
        var conversationId = Guid.NewGuid();

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PrePinArchiveMigrationId).ConfigureAwait(false);
        }

        await InsertHistoricalConversationAsync(databasePath, conversationId).ConfigureAwait(false);

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        var columns = await GetConversationColumnsAsync(connection).ConfigureAwait(false);
        AssertEx.True(columns.Contains("is_pinned"), "conversations.is_pinned should be added.");
        AssertEx.True(columns.Contains("archived"), "conversations.archived should be added.");

        AssertEx.False(await ReadIsPinnedAsync(connection, conversationId).ConfigureAwait(false), "Existing conversations should default to is_pinned = false.");
        AssertEx.False(await ReadArchivedAsync(connection, conversationId).ConfigureAwait(false), "Existing conversations should default to archived = false.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsPinArchiveColumns()
    {
        var databasePath = GetDatabasePath("pin-archive-rollback.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PrePinArchiveMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        var columns = await GetConversationColumnsAsync(connection).ConfigureAwait(false);
        AssertEx.False(columns.Contains("is_pinned"), "Rollback should drop conversations.is_pinned.");
        AssertEx.False(columns.Contains("archived"), "Rollback should drop conversations.archived.");
    }

    [Test]
    public async Task SaveChanges_WhenPinnedAndArchivedAreSet_PersistsMappedColumns()
    {
        var databasePath = GetDatabasePath("pin-archive-roundtrip.sqlite");
        var conversationId = Guid.NewGuid();

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);

            context.Conversations.Add(new NodeConversation
            {
                ConversationId = conversationId,
                CreatedAtUtc = 10,
                LastSeenUtc = 10,
                IsPinned = true,
                Archived = true
            });

            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            var conversation = await context.Conversations.SingleAsync(entity => entity.ConversationId == conversationId).ConfigureAwait(false);
            AssertEx.True(conversation.IsPinned, "is_pinned should round-trip as true.");
            AssertEx.True(conversation.Archived, "archived should round-trip as true.");
        }
    }

    private NodeChatDbContext CreateContext(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        var options = new DbContextOptionsBuilder<NodeChatDbContext>()
                      .UseSqlite($"Data Source={databasePath}")
                      .Options;

        return new NodeChatDbContext(options, _keyHolder);
    }

    private static async Task InsertHistoricalConversationAsync(string databasePath, Guid conversationId)
    {
        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO conversations (conversation_id, title, user_id, created_at_utc, last_seen_utc, purged)
                              VALUES ($conversation_id, $title, $user_id, $created_at_utc, $last_seen_utc, $purged);
                              """;
        command.Parameters.AddWithValue("$conversation_id", conversationId.ToString());
        command.Parameters.AddWithValue("$title", "Historical");
        command.Parameters.AddWithValue("$user_id", "node");
        command.Parameters.AddWithValue("$created_at_utc", 1234L);
        command.Parameters.AddWithValue("$last_seen_utc", 1234L);
        command.Parameters.AddWithValue("$purged", false);

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private static async Task<IReadOnlySet<string>> GetConversationColumnsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM conversations LIMIT 0;";

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        return Enumerable.Range(0, reader.FieldCount)
                         .Select(reader.GetName)
                         .ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<bool> ReadIsPinnedAsync(SqliteConnection connection, Guid conversationId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_pinned FROM conversations WHERE conversation_id = $id;";
        command.Parameters.AddWithValue("$id", conversationId.ToString());
        return await ReadBoolScalarAsync(command).ConfigureAwait(false);
    }

    private static async Task<bool> ReadArchivedAsync(SqliteConnection connection, Guid conversationId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT archived FROM conversations WHERE conversation_id = $id;";
        command.Parameters.AddWithValue("$id", conversationId.ToString());
        return await ReadBoolScalarAsync(command).ConfigureAwait(false);
    }

    private static async Task<bool> ReadBoolScalarAsync(SqliteCommand command)
    {
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
