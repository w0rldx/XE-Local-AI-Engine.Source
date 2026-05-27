namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class NodeChatBranchVariantFeedbackMigrationTests : IDisposable
{
    private const string PreBranchVariantMigrationId = "20260526122101_AddNodeConversationPinArchive";
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
    public async Task MigrateAsync_AddsBranchVariantColumnsAndFeedbackTable()
    {
        var databasePath = GetDatabasePath("branch-variant-up.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreBranchVariantMigrationId).ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        var messageColumns = await GetColumnsAsync(connection, "messages").ConfigureAwait(false);
        AssertEx.True(messageColumns.Contains("parent_message_id"), "messages.parent_message_id should be added.");
        AssertEx.True(messageColumns.Contains("variant_group_id"), "messages.variant_group_id should be added.");

        var conversationColumns = await GetColumnsAsync(connection, "conversations").ConfigureAwait(false);
        AssertEx.True(conversationColumns.Contains("branch_of_conversation_id"), "conversations.branch_of_conversation_id should be added.");

        AssertEx.True(await TableExistsAsync(connection, "message_feedback").ConfigureAwait(false), "message_feedback table should be created.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsBranchVariantColumnsAndFeedbackTable()
    {
        var databasePath = GetDatabasePath("branch-variant-rollback.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PreBranchVariantMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        var messageColumns = await GetColumnsAsync(connection, "messages").ConfigureAwait(false);
        AssertEx.False(messageColumns.Contains("parent_message_id"), "Rollback should drop messages.parent_message_id.");
        AssertEx.False(messageColumns.Contains("variant_group_id"), "Rollback should drop messages.variant_group_id.");

        var conversationColumns = await GetColumnsAsync(connection, "conversations").ConfigureAwait(false);
        AssertEx.False(conversationColumns.Contains("branch_of_conversation_id"), "Rollback should drop conversations.branch_of_conversation_id.");

        AssertEx.False(await TableExistsAsync(connection, "message_feedback").ConfigureAwait(false), "Rollback should drop the message_feedback table.");
    }

    [Test]
    public async Task SaveChanges_WhenBranchVariantAndFeedbackSet_PersistsMappedColumns()
    {
        var databasePath = GetDatabasePath("branch-variant-roundtrip.sqlite");
        var conversationId = Guid.NewGuid();
        var branchSourceId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var variantGroupId = Guid.NewGuid();

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);

            context.Conversations.Add(new NodeConversation
            {
                ConversationId = conversationId,
                CreatedAtUtc = 10,
                LastSeenUtc = 10,
                BranchOfConversationId = branchSourceId
            });

            context.Messages.Add(new NodeMessage
            {
                MessageId = messageId,
                ConversationId = conversationId,
                Sequence = 0,
                Role = "assistant",
                CreatedAtUtc = 11,
                UpdatedAtUtc = 11,
                ParentMessageId = parentId,
                VariantGroupId = variantGroupId
            });

            await context.SaveChangesAsync().ConfigureAwait(false);

            context.MessageFeedback.Add(new NodeMessageFeedback
            {
                MessageId = messageId,
                ConversationId = conversationId,
                Rating = NodeMessageFeedbackRating.Up,
                Comment = "helpful",
                CreatedAtUtc = 12,
                UpdatedAtUtc = 12
            });

            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            var conversation = await context.Conversations.SingleAsync(entity => entity.ConversationId == conversationId).ConfigureAwait(false);
            AssertEx.Equal(branchSourceId, conversation.BranchOfConversationId);

            var message = await context.Messages.SingleAsync(entity => entity.MessageId == messageId).ConfigureAwait(false);
            AssertEx.Equal(parentId, message.ParentMessageId);
            AssertEx.Equal(variantGroupId, message.VariantGroupId);

            var feedback = await context.MessageFeedback.SingleAsync(entity => entity.MessageId == messageId).ConfigureAwait(false);
            AssertEx.Equal(NodeMessageFeedbackRating.Up, feedback.Rating);
            AssertEx.Equal("helpful", feedback.Comment);
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

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private static async Task<IReadOnlySet<string>> GetColumnsAsync(SqliteConnection connection, string table)
    {
        // Constant per-table SQL (no interpolation) keeps the static SQL analyzer satisfied; the table name is
        // never user input, but mapping to literals documents that and avoids a CA2100 suppression.
        var commandText = table switch
        {
            "messages" => "SELECT * FROM messages LIMIT 0;",
            "conversations" => "SELECT * FROM conversations LIMIT 0;",
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Unsupported table for column inspection.")
        };

        await using var command = connection.CreateCommand();
        command.CommandText = commandText;

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        return Enumerable.Range(0, reader.FieldCount)
                         .Select(reader.GetName)
                         .ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
