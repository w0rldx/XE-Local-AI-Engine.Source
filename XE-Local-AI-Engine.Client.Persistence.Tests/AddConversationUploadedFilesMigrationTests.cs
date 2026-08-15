namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddConversationUploadedFiles</c> creates the chat attachment table. The cascade FK to <c>conversations</c> is
///     the load-bearing part: deleting a conversation must take its uploaded-file rows with it, or the purge leaves
///     orphaned rows pointing at blobs nothing will ever clean up.
/// </summary>
public sealed class AddConversationUploadedFilesMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_CreatesUploadedFilesBoundToConversations()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("conversation-uploaded-files.sqlite").ConfigureAwait(false);

        AssertEx.True(await probe.TableExistsAsync("conversation_uploaded_files").ConfigureAwait(false),
            "conversation_uploaded_files must exist.");

        var columns = await probe.ColumnsAsync("conversation_uploaded_files").ConfigureAwait(false);
        AssertEx.True(columns.IsSupersetOf(new[]
        {
            "file_id",
            "conversation_id",
            "original_file_name",
            "mime_type",
            "extension",
            "size_bytes",
            "extraction_status",
            "extracted_chars",
            "storage_path",
            "created_at_utc"
        }), "conversation_uploaded_files must expose the mapped columns.");

        AssertEx.True(await probe.ForeignKeyExistsAsync("conversation_uploaded_files", "conversation_id", "conversations").ConfigureAwait(false),
            "Uploaded files must be foreign-keyed to their conversation.");

        AssertEx.True(await probe.IndexExistsAsync("conversation_uploaded_files",
                "IX_conversation_uploaded_files_conversation_id",
                unique: false,
                "conversation_id").ConfigureAwait(false),
            "The per-conversation lookup must be indexed.");
    }
}
