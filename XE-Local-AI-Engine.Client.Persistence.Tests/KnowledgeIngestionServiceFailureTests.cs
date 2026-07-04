namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Ingestion state-machine failure path on a real database: when the embedding model cannot be resolved, the run must
///     end with <c>status = Failed</c> and a fixed, content-free <c>failure_reason</c> — never chunk or document text. The
///     embedder is driven with a provider resolver that throws the caught <see cref="InvalidOperationException" />, which
///     the real embedder maps to a content-free <see cref="KnowledgeIngestionException" />. The seeded document text is a
///     distinctive token that must NOT leak into the persisted failure reason.
/// </summary>
public sealed class KnowledgeIngestionServiceFailureTests : IDisposable
{
    private const string SecretDocumentText = "classifiedpayload42";

    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }

        _keyHolder.Dispose();
    }

    [Test]
    public async Task RunAsync_WhenNoEmbeddingModelIsAvailable_MarksTheDocumentFailedWithAContentFreeReason()
    {
        var databasePath = GetDatabasePath("ingestion-failure.sqlite");
        var documentId = Guid.NewGuid();

        await MigrateAsync(databasePath).ConfigureAwait(false);
        await SeedPendingDocumentAsync(databasePath, documentId).ConfigureAwait(false);

        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            await EnsureForeignKeysOffAsync(context.Database.GetDbConnection()).ConfigureAwait(false);
            var service = CreateService(context);
            await service.RunAsync(documentId, CancellationToken.None).ConfigureAwait(false);
        }

        var (status, failureReason) = await ReadStatusAsync(databasePath, documentId).ConfigureAwait(false);
        AssertEx.Equal(KnowledgeDocumentStatus.Failed.ToString(), status);
        var reason = AssertEx.NotNull(failureReason, "A failed ingestion should persist a failure reason.");
        AssertEx.True(reason.Contains("embedding model", StringComparison.OrdinalIgnoreCase),
            "The failure reason should describe the embedding-unavailable failure category.");
        AssertEx.False(reason.Contains(SecretDocumentText, StringComparison.Ordinal),
            "The persisted failure reason must never contain document text.");
    }

    private static KnowledgeIngestionService CreateService(NodeChatDbContext context)
    {
        var options = Options.Create(new KnowledgeBaseOptions());

        var blobStore = Substitute.For<IKnowledgeDocumentBlobStore>();
        blobStore.ReadBytesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<byte[]?>(new byte[] { 1, 2, 3 }));

        var extractor = Substitute.For<IDocumentTextExtractor>();
        extractor.ExtractStructuredAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new DocumentStructuredExtractionResult(DocumentExtractionStatus.Extracted, BuildExtractedDocument(), Error: null)));

        var embedder = new KnowledgeChunkEmbedder(new ThrowingProviderResolver(),
            new EmbeddingModelResolver(options),
            new KnowledgeEmbeddingPrefixer(),
            options);

        return new KnowledgeIngestionService(context,
            blobStore,
            extractor,
            new HeaderBoundaryChunkingService(options),
            embedder,
            Substitute.For<IKnowledgeIndexWriter>(),
            Substitute.For<IKnowledgeIndexingNotifier>(),
            TimeProvider.System,
            NullLogger<KnowledgeIngestionService>.Instance);
    }

    private static IngestionDocument BuildExtractedDocument()
    {
        var document = new IngestionDocument("test-document");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentHeader("Heading")
        {
            Text = "Heading",
            Level = 1
        });
        section.Elements.Add(new IngestionDocumentParagraph(SecretDocumentText)
        {
            Text = SecretDocumentText
        });
        document.Sections.Add(section);
        return document;
    }

    private async Task MigrateAsync(string databasePath)
    {
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        await context.Database.MigrateAsync().ConfigureAwait(false);
    }

    private static async Task SeedPendingDocumentAsync(string databasePath, Guid documentId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await EnsureForeignKeysOffAsync(connection).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO knowledge_documents (document_id, original_file_name, mime_type, extension, size_bytes, content_hash, storage_path, status, chunk_count, embedding_model, created_at_utc, updated_at_utc)
            VALUES ($id, $name, 'text/plain', '.txt', 10, $hash, $path, 'Pending', 0, 'nomic-embed-text', 1, 1);
            """;
        command.Parameters.AddWithValue("$id", documentId);
        command.Parameters.AddWithValue("$name", new byte[] { 1, 2, 3 });
        command.Parameters.AddWithValue("$hash", "hash-" + documentId.ToString("N"));
        command.Parameters.AddWithValue("$path", documentId.ToString("D") + ".txt");
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<(string Status, string? FailureReason)> ReadStatusAsync(string databasePath, Guid documentId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await EnsureForeignKeysOffAsync(connection).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, failure_reason FROM knowledge_documents WHERE document_id = $id;";
        command.Parameters.AddWithValue("$id", documentId);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        _ = await reader.ReadAsync().ConfigureAwait(false);
        var failureReason = await reader.IsDBNullAsync(1).ConfigureAwait(false) ? null : reader.GetString(1);
        return (reader.GetString(0), failureReason);
    }

    // Microsoft.Data.Sqlite enables foreign-key enforcement by default; the node-sqlite runtime connection does not,
    // so every KB test connection is aligned to that runtime mode.
    private static async Task EnsureForeignKeysOffAsync(System.Data.Common.DbConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync().ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = OFF;";
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    /// <summary>A provider resolver that always fails to resolve, standing in for "no embedding model available".</summary>
    private sealed class ThrowingProviderResolver : ILocalModelProviderResolver
    {
        public int MaxLoadedProcesses => 1;

        public ILocalModelProvider DefaultProvider => throw new InvalidOperationException("No provider is registered.");

        public Task<string> ResolveProviderNameForModelAsync(string modelName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult("llamacpp");
        }

        public ILocalModelProvider ResolveProvider(string providerName)
        {
            throw new InvalidOperationException("The embedding provider is not registered.");
        }

        public Task<ILocalModelProvider> ResolveProviderForModelAsync(string modelName, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The embedding provider is not registered.");
        }
    }
}
