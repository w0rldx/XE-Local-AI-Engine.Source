namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class NodeChatOriginMigrationTests : IDisposable
{
    private const string PreOriginMigrationId = "20260523133000_AddNodeMessageLifecycleColumns";
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
    public async Task MigrateAsync_WhenExistingRowsPresent_AddsOriginColumnsDefaultingToLocal()
    {
        var databasePath = GetDatabasePath("origin-defaults.sqlite");
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreOriginMigrationId).ConfigureAwait(false);
        }

        await InsertHistoricalRowsAsync(databasePath, conversationId, messageId).ConfigureAwait(false);

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        var conversationColumns = await GetConversationColumnsAsync(connection).ConfigureAwait(false);
        var messageColumns = await GetMessageColumnsAsync(connection).ConfigureAwait(false);

        AssertEx.True(conversationColumns.Contains("origin"), "conversations.origin should be added.");
        AssertEx.True(messageColumns.Contains("origin"), "messages.origin should be added.");

        AssertEx.Equal(NodeChatOrigin.Local,
            await ReadConversationOriginAsync(connection, conversationId).ConfigureAwait(false),
            "Existing conversations should default to Local origin.");
        AssertEx.Equal(NodeChatOrigin.Local,
            await ReadMessageOriginAsync(connection, messageId).ConfigureAwait(false),
            "Existing messages should default to Local origin.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsOriginColumns()
    {
        var databasePath = GetDatabasePath("origin-rollback.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PreOriginMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        var conversationColumns = await GetConversationColumnsAsync(connection).ConfigureAwait(false);
        var messageColumns = await GetMessageColumnsAsync(connection).ConfigureAwait(false);

        AssertEx.False(conversationColumns.Contains("origin"), "Rollback should drop conversations.origin.");
        AssertEx.False(messageColumns.Contains("origin"), "Rollback should drop messages.origin.");
    }

    [Test]
    public async Task SaveChanges_WhenRemoteOriginIsSet_PersistsMappedColumns()
    {
        var databasePath = GetDatabasePath("origin-remote.sqlite");
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);

            context.Conversations.Add(new NodeConversation
            {
                ConversationId = conversationId,
                CreatedAtUtc = 10,
                LastSeenUtc = 10,
                Origin = NodeChatOrigin.Remote
            });

            context.Messages.Add(new NodeMessage
            {
                MessageId = messageId,
                ConversationId = conversationId,
                Sequence = 1,
                Role = "assistant",
                Content = Encoding.UTF8.GetBytes("remote turn"),
                CreatedAtUtc = 11,
                UpdatedAtUtc = 12,
                Status = NodeMessageStatus.Completed,
                Origin = NodeChatOrigin.Remote
            });

            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            var conversation = await context.Conversations.SingleAsync(entity => entity.ConversationId == conversationId).ConfigureAwait(false);
            var message = await context.Messages.SingleAsync(entity => entity.MessageId == messageId).ConfigureAwait(false);

            AssertEx.Equal(NodeChatOrigin.Remote, conversation.Origin);
            AssertEx.Equal(NodeChatOrigin.Remote, message.Origin);
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

    private static async Task InsertHistoricalRowsAsync(string databasePath, Guid conversationId, Guid messageId)
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
            command.Parameters.AddWithValue("$created_at_utc", 1234L);
            command.Parameters.AddWithValue("$last_seen_utc", 1234L);
            command.Parameters.AddWithValue("$purged", false);

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                                  INSERT INTO messages (message_id, conversation_id, sequence, role, content, metadata_json, created_at_utc, updated_at_utc, status)
                                  VALUES ($message_id, $conversation_id, $sequence, $role, $content, $metadata_json, $created_at_utc, $updated_at_utc, $status);
                                  """;
            command.Parameters.AddWithValue("$message_id", messageId.ToString());
            command.Parameters.AddWithValue("$conversation_id", conversationId.ToString());
            command.Parameters.AddWithValue("$sequence", 1);
            command.Parameters.AddWithValue("$role", "assistant");
            command.Parameters.AddWithValue("$content", Encoding.UTF8.GetBytes("historical content"));
            command.Parameters.AddWithValue("$metadata_json", DBNull.Value);
            command.Parameters.AddWithValue("$created_at_utc", 1234L);
            command.Parameters.AddWithValue("$updated_at_utc", 1234L);
            command.Parameters.AddWithValue("$status", NodeMessageStatus.Completed);

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
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
        return await ReadColumnNamesAsync(command).ConfigureAwait(false);
    }

    private static async Task<IReadOnlySet<string>> GetMessageColumnsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM messages LIMIT 0;";
        return await ReadColumnNamesAsync(command).ConfigureAwait(false);
    }

    private static async Task<IReadOnlySet<string>> ReadColumnNamesAsync(SqliteCommand command)
    {
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        return Enumerable.Range(0, reader.FieldCount)
                         .Select(reader.GetName)
                         .ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<string> ReadConversationOriginAsync(SqliteConnection connection, Guid conversationId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT origin FROM conversations WHERE conversation_id = $id;";
        command.Parameters.AddWithValue("$id", conversationId.ToString());
        return await ReadOriginScalarAsync(command).ConfigureAwait(false);
    }

    private static async Task<string> ReadMessageOriginAsync(SqliteConnection connection, Guid messageId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT origin FROM messages WHERE message_id = $id;";
        command.Parameters.AddWithValue("$id", messageId.ToString());
        return await ReadOriginScalarAsync(command).ConfigureAwait(false);
    }

    private static async Task<string> ReadOriginScalarAsync(SqliteCommand command)
    {
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value as string ?? throw new AssertionException("Expected a non-null origin value.");
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
