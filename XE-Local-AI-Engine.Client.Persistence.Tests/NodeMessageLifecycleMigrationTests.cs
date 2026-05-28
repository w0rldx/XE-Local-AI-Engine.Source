namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class NodeMessageLifecycleMigrationTests : IDisposable
{
    private const string InitialMigrationId = "20260419152305_InitialNodeChatSchema";
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
    public async Task MigrateAsync_WhenExistingMessagesPresent_AddsLifecycleColumnsWithHistoricalDefaults()
    {
        var databasePath = GetDatabasePath("historical-defaults.sqlite");
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(InitialMigrationId).ConfigureAwait(false);
        }

        await InsertHistoricalMessageAsync(databasePath, conversationId, messageId, 1234).ConfigureAwait(false);

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        var columns = await GetMessageColumnsAsync(connection).ConfigureAwait(false);

        AssertEx.True(columns.Contains("status"), "messages.status should be added.");
        AssertEx.True(columns.Contains("updated_at_utc"), "messages.updated_at_utc should be added.");
        AssertEx.True(columns.Contains("request_id"), "messages.request_id should be added.");
        AssertEx.True(columns.Contains("error"), "messages.error should be added.");

        var migrated = await ReadMigratedMessageAsync(connection, messageId).ConfigureAwait(false);

        AssertEx.Equal(NodeMessageStatus.Completed, migrated.Status, "Existing messages should default to completed.");
        AssertEx.Equal(1234L, migrated.UpdatedAtUtc, "Existing messages should derive updated_at_utc from created_at_utc.");
        AssertEx.Null(migrated.RequestId, "Existing messages should not receive a synthetic request_id.");
        AssertEx.Null(migrated.Error, "Existing messages should not receive a synthetic error.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsLifecycleColumns()
    {
        var databasePath = GetDatabasePath("rollback.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(InitialMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        var columns = await GetMessageColumnsAsync(connection).ConfigureAwait(false);

        AssertEx.False(columns.Contains("status"), "Rollback should drop messages.status.");
        AssertEx.False(columns.Contains("updated_at_utc"), "Rollback should drop messages.updated_at_utc.");
        AssertEx.False(columns.Contains("request_id"), "Rollback should drop messages.request_id.");
        AssertEx.False(columns.Contains("error"), "Rollback should drop messages.error.");
    }

    [Test]
    public async Task SaveChanges_WhenNewLifecycleFieldsAreSet_PersistsMappedColumns()
    {
        var databasePath = GetDatabasePath("mapped-fields.sqlite");
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);

            context.Conversations.Add(new NodeConversation
            {
                ConversationId = conversationId,
                CreatedAtUtc = 10,
                LastSeenUtc = 10
            });

            context.Messages.Add(new NodeMessage
            {
                MessageId = messageId,
                ConversationId = conversationId,
                Sequence = 1,
                Role = "assistant",
                Content = Encoding.UTF8.GetBytes("partial"),
                CreatedAtUtc = 11,
                UpdatedAtUtc = 12,
                Status = NodeMessageStatus.Streaming,
                RequestId = requestId,
                Error = "provider timeout"
            });

            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            var message = await context.Messages.SingleAsync(entity => entity.MessageId == messageId).ConfigureAwait(false);

            AssertEx.Equal(NodeMessageStatus.Streaming, message.Status);
            AssertEx.Equal(12L, message.UpdatedAtUtc);
            AssertEx.Equal(requestId, message.RequestId);
            AssertEx.Equal("provider timeout", message.Error);
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

    private static async Task InsertHistoricalMessageAsync(string databasePath, Guid conversationId, Guid messageId, long createdAtUtc)
    {
        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                                  INSERT INTO conversations (conversation_id, title, user_id, created_at_utc, last_seen_utc, purged)
                                  VALUES ($conversation_id, $title, $user_id, $created_at_utc, $last_seen_utc, $purged);
                                  """;
            command.Parameters.AddWithValue("$conversation_id", conversationId.ToString());
            command.Parameters.AddWithValue("$title", "Historical");
            command.Parameters.AddWithValue("$user_id", "node");
            command.Parameters.AddWithValue("$created_at_utc", createdAtUtc);
            command.Parameters.AddWithValue("$last_seen_utc", createdAtUtc);
            command.Parameters.AddWithValue("$purged", false);

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                                  INSERT INTO messages (message_id, conversation_id, sequence, role, content, metadata_json, created_at_utc)
                                  VALUES ($message_id, $conversation_id, $sequence, $role, $content, $metadata_json, $created_at_utc);
                                  """;
            command.Parameters.AddWithValue("$message_id", messageId.ToString());
            command.Parameters.AddWithValue("$conversation_id", conversationId.ToString());
            command.Parameters.AddWithValue("$sequence", 1);
            command.Parameters.AddWithValue("$role", "assistant");
            command.Parameters.AddWithValue("$content", Encoding.UTF8.GetBytes("historical content"));
            command.Parameters.AddWithValue("$metadata_json", DBNull.Value);
            command.Parameters.AddWithValue("$created_at_utc", createdAtUtc);

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private static async Task<IReadOnlySet<string>> GetMessageColumnsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM messages LIMIT 0;";

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var names = Enumerable.Range(0, reader.FieldCount)
                              .Select(reader.GetName)
                              .ToHashSet(StringComparer.Ordinal);

        return names;
    }

    private static async Task<MigratedMessage> ReadMigratedMessageAsync(SqliteConnection connection, Guid messageId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, updated_at_utc, request_id, error FROM messages WHERE message_id = $message_id;";
        command.Parameters.AddWithValue("$message_id", messageId.ToString());

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            throw new AssertionException("Expected migrated message row to exist.");
        }

        return new MigratedMessage(reader.GetString(0),
            Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
            await reader.IsDBNullAsync(2).ConfigureAwait(false) ? null : Guid.Parse(reader.GetString(2)),
            await reader.IsDBNullAsync(3).ConfigureAwait(false) ? null : reader.GetString(3));
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    private readonly record struct MigratedMessage(string Status, long UpdatedAtUtc, Guid? RequestId, string? Error);
}
