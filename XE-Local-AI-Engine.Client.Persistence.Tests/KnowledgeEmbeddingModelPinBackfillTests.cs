namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     The startup backfill pins the embedder of a corpus indexed before the pin existed, only when the Indexed corpus names
///     exactly one model and that model is installed; it never overwrites a pin.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class KnowledgeEmbeddingModelPinBackfillTests : IDisposable
{
    private const string Nomic = "nomic-ai/nomic-embed-text-v1.5-GGUF:Q8_0";
    private const string Gemma = "ggml-org/embeddinggemma-2-GGUF:BF16";

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
    public async Task BackfillOnceAsync_WhenTheIndexedCorpusNamesOneInstalledModel_PinsIt()
    {
        // A Pending row under another model is not a built vector and must not make the corpus look mixed.
        var store = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var backfill = await CreateBackfillAsync("one-model.sqlite", store, [Indexed(Nomic), Indexed(Nomic), Pending(Gemma)], Nomic, Gemma);

        AssertEx.True(await backfill.BackfillOnceAsync(CancellationToken.None));
        AssertEx.Equal(Nomic, store.Current.KnowledgeEmbeddingModelName);
    }

    [Test]
    public async Task BackfillOnceAsync_WhenTheCorpusIsEmpty_LeavesThePinUnset()
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var backfill = await CreateBackfillAsync("empty.sqlite", store, [], Nomic);

        AssertEx.False(await backfill.BackfillOnceAsync(CancellationToken.None));
        AssertEx.Null(store.Current.KnowledgeEmbeddingModelName);
        AssertEx.Equal(0, store.WriteCount);
    }

    [Test]
    public async Task BackfillOnceAsync_WhenTheCorpusIsMixed_LeavesThePinUnset()
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var backfill = await CreateBackfillAsync("mixed.sqlite", store, [Indexed(Nomic), Indexed(Gemma)], Nomic, Gemma);

        AssertEx.False(await backfill.BackfillOnceAsync(CancellationToken.None));
        AssertEx.Null(store.Current.KnowledgeEmbeddingModelName);
    }

    [Test]
    public async Task BackfillOnceAsync_WhenTheCorpusModelIsNotInstalled_LeavesThePinUnset()
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var backfill = await CreateBackfillAsync("not-installed.sqlite", store, [Indexed(Nomic)], Gemma);

        AssertEx.False(await backfill.BackfillOnceAsync(CancellationToken.None));
        AssertEx.Null(store.Current.KnowledgeEmbeddingModelName);
    }

    [Test]
    public async Task BackfillOnceAsync_WhenAlreadyPinned_NeverOverwrites()
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings
        {
            KnowledgeEmbeddingModelName = Gemma
        });
        using var backfill = await CreateBackfillAsync("pinned.sqlite", store, [Indexed(Nomic)], Nomic, Gemma);

        AssertEx.False(await backfill.BackfillOnceAsync(CancellationToken.None));
        AssertEx.Equal(Gemma, store.Current.KnowledgeEmbeddingModelName);
        AssertEx.Equal(0, store.WriteCount);
    }

    private async Task<KnowledgeEmbeddingModelPinBackfill> CreateBackfillAsync(string fileName,
        INodeSettingsStore store,
        IReadOnlyList<SeededDocument> documents,
        params string[] installed)
    {
        Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, fileName);
        await MigratedDatabaseTemplate.CopyChatHeadAsync(databasePath);
        foreach (var document in documents)
        {
            await SeedDocumentAsync(databasePath, document.Model, document.Status);
        }

        var provider = Substitute.For<ILocalModelProvider>();
        provider.ListModelsAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<LocalModelDescriptor>>(installed.Select(static name => new LocalModelDescriptor
                                                                                       {
                                                                                           ModelName = name,
                                                                                           ProviderName = "llamacpp",
                                                                                           IsAvailable = true,
                                                                                           SizeBytes = 1024,
                                                                                           ModifiedAt = DateTimeOffset.UnixEpoch,
                                                                                           MaxContextTokens = null,
                                                                                           Capabilities = []
                                                                                       })
                                                                                       .ToList()));
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProvider(Arg.Any<string>()).Returns(provider);

        var options = Options.Create(new KnowledgeBaseOptions());
        var scopeFactory = new ServiceCollection()
                           .AddScoped(_ => AgentDefinitionTestContextFactory.Create(databasePath, _keyHolder))
                           .AddScoped<IKnowledgeDocumentCatalogService>(services => new KnowledgeDocumentCatalogService(
                               services.GetRequiredService<NodeChatDbContext>(),
                               providerResolver,
                               new EmbeddingModelResolver(options, store),
                               options,
                               TimeProvider.System))
                           .BuildServiceProvider()
                           .GetRequiredService<IServiceScopeFactory>();
        return new KnowledgeEmbeddingModelPinBackfill(scopeFactory,
            store,
            providerResolver,
            options,
            NullLogger<KnowledgeEmbeddingModelPinBackfill>.Instance);
    }

    private async Task SeedDocumentAsync(string databasePath, string embeddingModel, string status)
    {
        var documentId = Guid.NewGuid();
        byte[] encryptedName;
        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            encryptedName = context.EncryptKnowledgeFileName("document.txt", documentId);
        }

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO knowledge_documents (document_id, original_file_name, mime_type, extension, size_bytes, content_hash, storage_path, status, chunk_count, embedding_model, created_at_utc, updated_at_utc)
            VALUES ($id, $name, 'text/plain', '.txt', 10, $hash, $path, $status, 0, $model, 1, 1);
            """;
        command.Parameters.AddWithValue("$id", documentId);
        command.Parameters.AddWithValue("$name", encryptedName);
        command.Parameters.AddWithValue("$hash", "hash-" + documentId.ToString("N"));
        command.Parameters.AddWithValue("$path", documentId.ToString("D") + ".txt");
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$model", embeddingModel);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static SeededDocument Indexed(string model) =>
        new()
        {
            Model = model,
            Status = "Indexed"
        };

    private static SeededDocument Pending(string model) =>
        new()
        {
            Model = model,
            Status = "Pending"
        };

    private sealed class SeededDocument
    {
        public required string Model { get; init; }

        public required string Status { get; init; }
    }
}
