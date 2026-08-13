namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Persistence;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;

public sealed class KnowledgeDowngradeSafetyServiceTests : IDisposable
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
    public async Task PreflightAsync_CompatibleDataset_IsReadOnlyAndCompatible()
    {
        var databasePath = GetDatabasePath("compatible.sqlite");
        await using var serviceProvider = await BuildMigratedServiceProviderAsync(databasePath).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, Guid.NewGuid(), "COLLECTION-A", "hash-a").ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, Guid.NewGuid(), "COLLECTION-B", "hash-b").ConfigureAwait(false);

        var service = serviceProvider.GetRequiredService<IKnowledgeDowngradeSafetyService>();
        var result = await service.PreflightAsync().ConfigureAwait(false);

        AssertEx.True(result.CollectionMigrationApplied);
        AssertEx.True(result.IsCompatible);
        AssertEx.Equal(0, result.ConflictGroupCount);
        AssertEx.Equal(0, result.ConflictingDocumentCount);
        AssertEx.Equal(0, result.MinimumDocumentsToRemove);
        AssertEx.Empty(result.Conflicts);
        AssertEx.Equal(2L, await CountDocumentsAsync(databasePath).ConfigureAwait(false),
            "A preflight must not modify compatible data.");
    }

    [Test]
    public async Task PreflightAsync_ConflictingDataset_ReturnsDeterministicOpaqueIdentifiersWithoutContentMetadata()
    {
        var databasePath = GetDatabasePath("conflicts.sqlite");
        await using var serviceProvider = await BuildMigratedServiceProviderAsync(databasePath).ConfigureAwait(false);
        var first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var second = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var third = Guid.Parse("33333333-3333-3333-3333-333333333333");
        await SeedDocumentAsync(databasePath, first, "COLLECTION-A", "sensitive-content-hash", "secret-a.txt").ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, second, "COLLECTION-B", "sensitive-content-hash", "secret-b.txt").ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, third, "COLLECTION-C", "sensitive-content-hash", "secret-c.txt").ConfigureAwait(false);

        var service = serviceProvider.GetRequiredService<IKnowledgeDowngradeSafetyService>();
        var firstResult = await service.PreflightAsync().ConfigureAwait(false);
        var secondResult = await service.PreflightAsync().ConfigureAwait(false);

        AssertEx.False(firstResult.IsCompatible);
        AssertEx.Equal(1, firstResult.ConflictGroupCount);
        AssertEx.Equal(3, firstResult.ConflictingDocumentCount);
        AssertEx.Equal(2, firstResult.MinimumDocumentsToRemove);
        var conflict = AssertEx.NotNull(firstResult.Conflicts.Single());
        AssertEx.Equal("conflict-000001", conflict.ConflictId);
        AssertEx.Equal(3, conflict.DocumentIdentifiers.Count);
        AssertEx.True(conflict.DocumentIdentifiers.All(static identifier =>
            identifier.StartsWith("document-", StringComparison.Ordinal) && identifier.Length == 73));
        AssertEx.Equal(string.Join('|', conflict.DocumentIdentifiers),
            string.Join('|', secondResult.Conflicts.Single().DocumentIdentifiers),
            "Opaque identifiers and their ordering must be deterministic across runs.");

        var serializedSurface = string.Join('|', conflict.DocumentIdentifiers);
        AssertEx.False(serializedSurface.Contains("sensitive-content-hash", StringComparison.Ordinal));
        AssertEx.False(serializedSurface.Contains("secret-", StringComparison.Ordinal));
        AssertEx.False(serializedSurface.Contains(first.ToString(), StringComparison.OrdinalIgnoreCase));
        AssertEx.Equal(3L, await CountDocumentsAsync(databasePath).ConfigureAwait(false),
            "A conflicting preflight must report only; it must not resolve or delete data.");
    }

    [Test]
    public async Task ExportAsync_ConflictDataset_WritesConsistentBackupWithoutResolvingConflict()
    {
        var databasePath = GetDatabasePath("export.sqlite");
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 8, 13, 12, 34, 56, TimeSpan.Zero));
        await using var serviceProvider = await BuildMigratedServiceProviderAsync(databasePath, clock).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, Guid.NewGuid(), "COLLECTION-A", "duplicate").ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, Guid.NewGuid(), "COLLECTION-B", "duplicate").ConfigureAwait(false);

        var result = await serviceProvider.GetRequiredService<IKnowledgeDowngradeSafetyService>()
                                          .ExportAsync()
                                          .ConfigureAwait(false);

        var expectedDirectory = Path.Combine(_rootPath, "backups", "knowledge-downgrade");
        AssertEx.Equal(Path.Combine(expectedDirectory, "node-chat-before-knowledge-downgrade-20260813T123456000Z.sqlite"),
            result.ArtifactPath);
        AssertEx.True(File.Exists(result.ArtifactPath));
        AssertEx.True(result.ArtifactBytes > 0);
        AssertEx.Equal(64, result.ArtifactSha256.Length);
        AssertEx.False(result.Preflight.IsCompatible,
            "Export is a safety artifact, not destructive conflict resolution, so it must still report the block.");
        AssertEx.Equal(2L, await CountDocumentsAsync(result.ArtifactPath).ConfigureAwait(false));
        AssertEx.Equal(2L, await CountDocumentsAsync(databasePath).ConfigureAwait(false));
    }

    [Test]
    public async Task ExportAsync_WhenBackupDirectoryIsSymlink_RejectsPathWithoutWritingOutsideRoot()
    {
        var databasePath = GetDatabasePath("symlink.sqlite");
        await using var serviceProvider = await BuildMigratedServiceProviderAsync(databasePath).ConfigureAwait(false);
        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_rootPath, "backups"), outside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Directory.Delete(outside, recursive: true);
            Skip.Test($"This host cannot create the symlink needed by the path-safety test: {exception.Message}");
            return;
        }

        try
        {
            _ = await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() =>
                    serviceProvider.GetRequiredService<IKnowledgeDowngradeSafetyService>().ExportAsync())
                .ConfigureAwait(false);
            AssertEx.Empty(Directory.EnumerateFileSystemEntries(outside),
                "A symlinked backup directory must not redirect an export outside the node data root.");
        }
        finally
        {
            Directory.Delete(Path.Combine(_rootPath, "backups"));
            Directory.Delete(outside, recursive: true);
        }
    }

    [Test]
    public async Task ExportAsync_WhenAlreadyCancelled_DoesNotCreateBackupDirectory()
    {
        var databasePath = GetDatabasePath("cancelled.sqlite");
        await using var serviceProvider = await BuildMigratedServiceProviderAsync(databasePath).ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() =>
                serviceProvider.GetRequiredService<IKnowledgeDowngradeSafetyService>().ExportAsync(cancellation.Token))
            .ConfigureAwait(false);

        AssertEx.False(Directory.Exists(Path.Combine(_rootPath, "backups")),
            "Cancellation before work begins must not leave an export directory or partial artifact.");
    }

    [Test]
    public async Task ExportAsync_WhenBackupPathCollidesWithFile_PropagatesFailure()
    {
        var databasePath = GetDatabasePath("failure.sqlite");
        await using var serviceProvider = await BuildMigratedServiceProviderAsync(databasePath).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "backups"), "collision").ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<IOException>(() =>
                serviceProvider.GetRequiredService<IKnowledgeDowngradeSafetyService>().ExportAsync())
            .ConfigureAwait(false);
    }

    private async Task<ServiceProvider> BuildMigratedServiceProviderAsync(string databasePath, TimeProvider? timeProvider = null)
    {
        var connectionString = $"Data Source={databasePath}";
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                ["ConnectionStrings:node-sqlite"] = connectionString
                            })
                            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddSingleton<INodeDataDirectory>(new FixedNodeDataDirectory(_rootPath));
        services.AddSingleton(timeProvider ?? TimeProvider.System);
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton<IKnowledgeDowngradeSafetyService, KnowledgeDowngradeSafetyService>();

        var provider = services.BuildServiceProvider(true);
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NodeChatDbContext>()
                   .Database.MigrateAsync()
                   .ConfigureAwait(false);
        return provider;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    private static async Task SeedDocumentAsync(string databasePath,
        Guid documentId,
        string collectionId,
        string contentHash,
        string fileName = "document.txt")
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO knowledge_documents
                (document_id, original_file_name, mime_type, extension, size_bytes, content_hash, storage_path, status,
                 chunk_count, embedding_model, vector_identity, collection_id, created_at_utc, updated_at_utc)
            VALUES
                ($id, $name, 'text/plain', '.txt', 10, $hash, $path, 'Indexed', 0, 'embed', 'vector', $collection, 1, 1);
            """;
        command.Parameters.AddWithValue("$id", documentId);
        command.Parameters.AddWithValue("$name", RandomNumberGenerator.GetBytes(48));
        command.Parameters.AddWithValue("$hash", contentHash);
        command.Parameters.AddWithValue("$path", fileName);
        command.Parameters.AddWithValue("$collection", collectionId);
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<long> CountDocumentsAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM knowledge_documents;";
        return Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false));
    }

    private sealed class FixedNodeDataDirectory(string root) : INodeDataDirectory
    {
        public string Root { get; } = root;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
