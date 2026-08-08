namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Providers.Abstractions;

public sealed class ConversationUploadedFileStoreTests : IDisposable
{
    private const string OriginalFileName = "secret-quarterly-report.pdf";
    private const string ExtractedMarkdown = "# Quarterly report\nThe classified revenue figure is 4815162342.";

    // Share one explicit EF internal service provider across this fixture's contexts so EF does not auto-create a new
    // internal provider per options config (which would trip the process-global ManyServiceProvidersCreatedWarning).
    private static readonly IServiceProvider SharedEfServiceProvider = new ServiceCollection()
                                                                       .AddEntityFrameworkSqlite()
                                                                       .BuildServiceProvider();

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task AddAsync_RoundTrips_AndEncryptsBytesNameAndMarkdownAtRest()
    {
        var databasePath = GetDatabasePath("roundtrip.sqlite");
        var uploadRoot = Path.Combine(_rootPath, "data");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var provider = await BuildProviderAsync(databasePath, keyHolder).ConfigureAwait(false);
        var store = CreateStore(provider, uploadRoot, keyHolder);
        var service = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>(), store);

        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Title", "user", CreatedAtUtc: 1000)).ConfigureAwait(false);
        var fileId = Guid.NewGuid();
        var content = Encoding.UTF8.GetBytes("PLAINTEXT-FILE-BODY-4815162342-should-be-encrypted");

        var info = await store.AddAsync(new ConversationUploadedFileInput(conversation.ConversationId,
            fileId,
            OriginalFileName,
            "application/pdf",
            "PDF",
            content.Length,
            content,
            DocumentExtractionStatus.Extracted,
            ExtractedMarkdown,
            ExtractedMarkdown.Length), CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(fileId, info.FileId);
        AssertEx.Equal(OriginalFileName, info.OriginalFileName);
        AssertEx.Equal(".pdf", info.Extension);
        AssertEx.Equal(DocumentExtractionStatus.Extracted, info.ExtractionStatus);
        AssertEx.True(info.ExtractedChars == ExtractedMarkdown.Length, "Extracted char count should round-trip.");

        // Bytes on disk must be ciphertext: neither the raw body nor the unique plaintext marker may appear.
        var bytesPath = Path.Combine(uploadRoot, "uploaded-files", "conversations", conversation.ConversationId.ToString("D"), fileId.ToString("D") + ".pdf");
        AssertEx.True(File.Exists(bytesPath), "Encrypted bytes file should be written to disk.");
        var diskBytes = await File.ReadAllBytesAsync(bytesPath).ConfigureAwait(false);
        AssertEx.False(ContainsSubsequence(diskBytes, content), "On-disk bytes should not contain the plaintext file body.");

        // Cached extracted Markdown on disk must be ciphertext, but ReadExtractedMarkdownAsync round-trips it.
        var markdownPath = Path.Combine(uploadRoot, "uploaded-files", "conversations", conversation.ConversationId.ToString("D"), fileId.ToString("D") + ".md");
        var diskMarkdown = await File.ReadAllBytesAsync(markdownPath).ConfigureAwait(false);
        AssertEx.False(ContainsSubsequence(diskMarkdown, Encoding.UTF8.GetBytes(ExtractedMarkdown)), "On-disk Markdown should be encrypted at rest.");
        AssertEx.Equal(ExtractedMarkdown, await store.ReadExtractedMarkdownAsync(conversation.ConversationId, fileId, CancellationToken.None).ConfigureAwait(false));

        // The display name column must be ciphertext in the database file.
        AssertEx.False(await DatabaseContainsAsync(databasePath, Encoding.UTF8.GetBytes(OriginalFileName)).ConfigureAwait(false),
            "The SQLite file should not contain the plaintext uploaded file name.");

        // List decrypts the name and metadata back.
        var listed = await store.ListAsync(conversation.ConversationId, CancellationToken.None).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, listed.Count);
        AssertEx.Equal(OriginalFileName, listed[0].OriginalFileName);
        AssertEx.Equal(content.Length, listed[0].SizeBytes);
    }

    [Test]
    public async Task DeleteConversation_WhenPurged_RemovesUploadedRowsAndDiskFiles()
    {
        var databasePath = GetDatabasePath("cascade.sqlite");
        var uploadRoot = Path.Combine(_rootPath, "cascade-data");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var provider = await BuildProviderAsync(databasePath, keyHolder).ConfigureAwait(false);
        var store = CreateStore(provider, uploadRoot, keyHolder);
        var service = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>(), store);

        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Title", "user", CreatedAtUtc: 1000)).ConfigureAwait(false);
        await AddSampleFileAsync(store, conversation.ConversationId, "alpha.txt").ConfigureAwait(false);
        await AddSampleFileAsync(store, conversation.ConversationId, "beta.txt").ConfigureAwait(false);

        var conversationDirectory = Path.Combine(uploadRoot, "uploaded-files", "conversations", conversation.ConversationId.ToString("D"));
        AssertEx.True(Directory.Exists(conversationDirectory), "Upload directory should exist before delete.");
        AssertEx.Equal(expected: 2, (await store.ListAsync(conversation.ConversationId, CancellationToken.None).ConfigureAwait(false)).Count);

        _ = await service.DeleteConversationAsync(new NodeChatDeleteConversationRequest(conversation.ConversationId, DeletedAtUtc: 2000, PurgeImmediately: true)).ConfigureAwait(false);

        AssertEx.Empty(await store.ListAsync(conversation.ConversationId, CancellationToken.None).ConfigureAwait(false));
        AssertEx.False(Directory.Exists(conversationDirectory), "Upload directory should be removed after purge.");
    }

    [Test]
    public async Task CreateStagingSnapshot_DecryptsMarkdownAndDisposeRemovesDirectory()
    {
        var databasePath = GetDatabasePath("staging.sqlite");
        var uploadRoot = Path.Combine(_rootPath, "staging-data");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var provider = await BuildProviderAsync(databasePath, keyHolder).ConfigureAwait(false);
        var store = CreateStore(provider, uploadRoot, keyHolder);
        var service = new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>(), store);

        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Title", "user", CreatedAtUtc: 1000)).ConfigureAwait(false);
        await AddSampleFileAsync(store, conversation.ConversationId, "notes.txt").ConfigureAwait(false);
        await AddSampleFileAsync(store, conversation.ConversationId, "summary.txt").ConfigureAwait(false);

        string hostPath;
        await using (var snapshot = await store.CreateStagingSnapshotAsync(conversation.ConversationId, CancellationToken.None).ConfigureAwait(false))
        {
            AssertEx.Equal(expected: 2, snapshot.FileCount);
            hostPath = snapshot.HostPath;
            AssertEx.True(Directory.Exists(hostPath), "Staging directory should exist while the snapshot is alive.");

            var stagedFiles = Directory.GetFiles(hostPath, "*.md");
            AssertEx.Equal(expected: 2, stagedFiles.Length);
            foreach (var stagedFile in stagedFiles)
            {
                var text = await File.ReadAllTextAsync(stagedFile).ConfigureAwait(false);
                AssertEx.True(text.Contains(ExtractedMarkdown, StringComparison.Ordinal), "Staged Markdown should be the decrypted extracted text.");
            }
        }

        AssertEx.False(Directory.Exists(hostPath), "Disposing the snapshot should remove the staging directory.");
    }

    [Test]
    public async Task Migrate_CreatesConversationUploadedFilesTableWithForeignKeyAndIndex()
    {
        var databasePath = GetDatabasePath("migrate.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);

        var columns = await GetUploadedFileColumnsAsync(connection).ConfigureAwait(false);
        AssertEx.True(columns.SetEquals(new[]
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
            }),
            "conversation_uploaded_files should expose the mapped columns.");
        AssertEx.True(await HasConversationForeignKeyAsync(connection).ConfigureAwait(false),
            "conversation_uploaded_files.conversation_id should be a cascading foreign key to conversations.");
        AssertEx.True(await HasConversationIndexAsync(connection).ConfigureAwait(false),
            "conversation_uploaded_files.conversation_id should be indexed.");
    }

    private static async Task AddSampleFileAsync(IConversationUploadedFileStore store, Guid conversationId, string fileName)
    {
        var content = Encoding.UTF8.GetBytes("body-of-" + fileName);
        _ = await store.AddAsync(new ConversationUploadedFileInput(conversationId,
            Guid.NewGuid(),
            fileName,
            "text/plain",
            ".txt",
            content.Length,
            content,
            DocumentExtractionStatus.Extracted,
            ExtractedMarkdown,
            ExtractedMarkdown.Length), CancellationToken.None).ConfigureAwait(false);
    }

    private static ConversationUploadedFileStore CreateStore(ServiceProvider provider, string uploadRoot, INodeSqliteKeyHolder keyHolder)
    {
        return new ConversationUploadedFileStore(provider.GetRequiredService<IServiceScopeFactory>(),
            new FixedNodeDataDirectory(uploadRoot),
            keyHolder,
            TimeProvider.System);
    }

    private static async Task<ServiceProvider> BuildProviderAsync(string databasePath, INodeSqliteKeyHolder keyHolder)
    {
        var services = new ServiceCollection();
        services.AddSingleton(keyHolder);
        services.AddDbContext<NodeChatDbContext>(options => options
                                                            .UseSqlite($"Data Source={databasePath}")
                                                            .UseInternalServiceProvider(SharedEfServiceProvider));
        services.AddSingleton<NodeChatPersistenceWriter>();

        var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.EnsureDeletedAsync().ConfigureAwait(false);
        await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);

        return provider;
    }

    private static NodeChatDbContext CreateContext(string databasePath, INodeSqliteKeyHolder keyHolder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        // Migration application needs no at-rest interceptors; reuse the shared internal provider so this context adds
        // no new EF internal service provider to the process-global count.
        var options = new DbContextOptionsBuilder<NodeChatDbContext>()
                      .UseSqlite($"Data Source={databasePath}")
                      .UseInternalServiceProvider(SharedEfServiceProvider)
                      .Options;

        return new NodeChatDbContext(options, keyHolder);
    }

    private static async Task<IReadOnlySet<string>> GetUploadedFileColumnsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM conversation_uploaded_files LIMIT 0;";

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        return Enumerable.Range(start: 0, reader.FieldCount)
                         .Select(reader.GetName)
                         .ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<bool> HasConversationForeignKeyAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"table\", \"on_delete\" FROM pragma_foreign_key_list('conversation_uploaded_files');";
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(0), "conversations", StringComparison.Ordinal)
                && reader.GetString(1).Contains("CASCADE", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> HasConversationIndexAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND tbl_name = 'conversation_uploaded_files' AND sql LIKE '%conversation_id%';";
        return await command.ExecuteScalarAsync().ConfigureAwait(false) is not null;
    }

    private static async Task<bool> DatabaseContainsAsync(string databasePath, byte[] needle)
    {
        var fileBytes = await SqliteFileProbe.ReadAllBytesAsync(databasePath).ConfigureAwait(false);
        return ContainsSubsequence(fileBytes, needle);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    private static byte[] CreateKeyMaterial()
    {
        return Enumerable.Range(start: 0, count: 32).Select(static value => (byte)(value + 7)).ToArray();
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

    private sealed class FixedNodeDataDirectory(string root) : INodeDataDirectory
    {
        public string Root { get; } = root;
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
