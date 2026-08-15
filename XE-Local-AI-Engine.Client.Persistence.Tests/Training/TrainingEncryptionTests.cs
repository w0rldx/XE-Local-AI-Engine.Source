namespace XE_Local_AI_Engine.Client.Persistence.Tests.Training;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Covers both interceptor directions for the training tables. A column registered for encryption but not for
///     decryption (or the reverse) round-trips as garbage rather than failing loudly, so every new encrypted column is
///     asserted here as ciphertext at rest AND plaintext after a fresh read.
/// </summary>
public sealed class TrainingEncryptionTests : IDisposable
{
    private const string DefinitionJson = """{"toolNames":["read_file"],"sizeTarget":32,"holdoutFraction":0.1}""";
    private const string SampleContentJson = """[{"kind":"tool","name":"read_file","args":{"path":"README.md"}}]""";
    private const string SampleValidationJson = """{"schema":"pass","execution":"mocked"}""";
    private const string MockJson = """{"rules":[{"match":{"path":"README.md"},"response":"# Title"}]}""";
    private const string MockVerificationJson = """{"state":"verified","findings":[]}""";
    private const string FilesJson = """[{"role":"weights","fileName":"model.safetensors","sizeBytes":42,"sha256":"ab"}]""";
    private const string LicenseJson = """{"license":"apache-2.0","gated":false}""";

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task TrainingPayloads_WhenSavedAndReadBack_RoundTripThroughBothInterceptors()
    {
        var databasePath = GetDatabasePath("roundtrip.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        var datasetId = await SeedAsync(databasePath, keyHolder);

        await using var readContext = AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
        var definition = AssertEx.NotNull(await readContext.TrainingDatasetDefinitions.SingleAsync(), "Definition should be readable.");
        AssertEx.Equal(DefinitionJson, Encoding.UTF8.GetString(definition.DefinitionJson));

        var sample = AssertEx.NotNull(await readContext.TrainingDatasetSamples.SingleAsync(), "Sample should be readable.");
        AssertEx.Equal(SampleContentJson, Encoding.UTF8.GetString(sample.ContentJson));
        AssertEx.Equal(SampleValidationJson, Encoding.UTF8.GetString(sample.ValidationJson!));
        AssertEx.Equal(datasetId, sample.DatasetId);

        var mock = AssertEx.NotNull(await readContext.ToolMockDefinitions.SingleAsync(), "Mock should be readable.");
        AssertEx.Equal(MockJson, Encoding.UTF8.GetString(mock.MockJson));
        AssertEx.Equal(MockVerificationJson, Encoding.UTF8.GetString(mock.VerificationJson!));

        var artifact = AssertEx.NotNull(await readContext.TrainingBaseArtifacts.SingleAsync(), "Base artifact should be readable.");
        AssertEx.Equal(FilesJson, Encoding.UTF8.GetString(artifact.FilesJson));
        AssertEx.Equal(LicenseJson, Encoding.UTF8.GetString(artifact.LicenseJson!));
    }

    [Test]
    public async Task TrainingPayloads_WhenPersisted_AreCiphertextAtRest()
    {
        var databasePath = GetDatabasePath("at-rest.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        _ = await SeedAsync(databasePath, keyHolder);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();

        // One literal statement per table — CA2100 rejects a composed command text, so the columns cannot be looped over.
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT definition_json FROM training_dataset_definitions LIMIT 1;";
            await AssertCiphertextAsync(command, DefinitionJson);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT content_json, validation_json FROM training_dataset_samples LIMIT 1;";
            await AssertCiphertextAsync(command, SampleContentJson, SampleValidationJson);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT mock_json, verification_json FROM tool_mock_definitions LIMIT 1;";
            await AssertCiphertextAsync(command, MockJson, MockVerificationJson);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT files_json, license_json FROM training_base_artifacts LIMIT 1;";
            await AssertCiphertextAsync(command, FilesJson, LicenseJson);
        }
    }

    [Test]
    public async Task SampleCiphertext_WhenReparentedToAnotherDataset_FailsToDecrypt()
    {
        var databasePath = GetDatabasePath("reparent.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        _ = await SeedAsync(databasePath, keyHolder);

        // A database WRITER moves the sample row onto the second dataset without touching its ciphertext. The AAD binds
        // the owning dataset id, so the tag check fails rather than surfacing the sample under the wrong dataset.
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // Selected as a subquery rather than a bound Guid parameter so the id text matches EF's own storage format
            // byte for byte — otherwise the move fails the foreign key instead of reaching the tag check under test.
            command.CommandText = "UPDATE training_dataset_samples SET dataset_id = (SELECT id FROM training_datasets WHERE name = 'Other');";
            AssertEx.Equal(expected: 1, await command.ExecuteNonQueryAsync());
        }

        await using var readContext = AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
        _ = await AssertEx.ThrowsAsync<CryptographicException>(async () => _ = await readContext.TrainingDatasetSamples.SingleAsync(),
            "Re-parented sample ciphertext must fail the AEAD tag check.");
    }

    /// <summary>Seeds one row per training table and returns the primary dataset's id. A second dataset exists so the
    ///     re-parenting test has a real target row to move a sample onto.</summary>
    private static async Task<Guid> SeedAsync(string databasePath, INodeSqliteKeyHolder keyHolder)
    {
        await using var context = AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var definition = new TrainingDatasetDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Tool calling v1",
            Kind = TrainingDatasetKind.ToolCalling,
            DefinitionJson = Encoding.UTF8.GetBytes(DefinitionJson),
            DefinitionVersion = 1,
            Version = 1,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1
        };
        var dataset = CreateDataset(definition.Id, "Primary");
        var otherDataset = CreateDataset(definition.Id, "Other");
        var sample = new TrainingDatasetSample
        {
            Id = Guid.NewGuid(),
            DatasetId = dataset.Id,
            Sequence = 0,
            Kind = "single-tool-call",
            Label = TrainingSampleLabel.Good,
            ReviewState = TrainingSampleReviewState.Pending,
            ContentJson = Encoding.UTF8.GetBytes(SampleContentJson),
            ValidationJson = Encoding.UTF8.GetBytes(SampleValidationJson),
            Provenance = TrainingSampleProvenance.Generated,
            SourceHash = new string('a', count: 64),
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1
        };
        var mock = new ToolMockDefinition
        {
            Id = Guid.NewGuid(),
            ToolName = "read_file",
            MockJson = Encoding.UTF8.GetBytes(MockJson),
            VerificationJson = Encoding.UTF8.GetBytes(MockVerificationJson),
            VerificationState = ToolMockVerificationState.Verified,
            Enabled = true,
            Version = 1,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1
        };
        var artifact = new TrainingBaseArtifact
        {
            Id = Guid.NewGuid(),
            RepoId = "org/base-model",
            Revision = new string('b', count: 40),
            Status = TrainingBaseArtifactStatus.Ready,
            FilesJson = Encoding.UTF8.GetBytes(FilesJson),
            TotalBytes = 42,
            LicenseJson = Encoding.UTF8.GetBytes(LicenseJson),
            Version = 1,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1
        };

        context.TrainingDatasetDefinitions.Add(definition);
        context.TrainingDatasets.AddRange(dataset, otherDataset);
        context.TrainingDatasetSamples.Add(sample);
        context.ToolMockDefinitions.Add(mock);
        context.TrainingBaseArtifacts.Add(artifact);
        context.DatasetGenerationWorkItems.Add(new DatasetGenerationWorkItem
        {
            DatasetId = dataset.Id,
            Status = DatasetGenerationWorkStatus.Queued,
            Attempt = 1,
            Version = 1,
            EnqueuedAtUtc = 1
        });
        await context.SaveChangesAsync();

        // The save-changes interceptor restores plaintext onto the tracked graph after the flush, so the in-memory
        // entity never observes ciphertext.
        AssertEx.Equal(SampleContentJson, Encoding.UTF8.GetString(sample.ContentJson));

        return dataset.Id;
    }

    private static TrainingDataset CreateDataset(Guid definitionId, string name)
    {
        return new TrainingDataset
        {
            Id = Guid.NewGuid(),
            DefinitionId = definitionId,
            DefinitionVersion = 1,
            Name = name,
            Status = TrainingDatasetStatus.Generating,
            Revision = 1,
            Version = 1,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1
        };
    }

    private static async Task AssertCiphertextAsync(SqliteCommand command, params string[] plaintexts)
    {
        await using var reader = await command.ExecuteReaderAsync();
        AssertEx.True(await reader.ReadAsync(), "Expected a seeded row to inspect.");

        for (var index = 0; index < plaintexts.Length; index++)
        {
            var stored = reader.GetValue(index) as byte[] ?? throw new AssertionException($"Expected a non-null BLOB in {reader.GetName(index)}.");
            AssertEx.False(stored.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(plaintexts[index])),
                $"{reader.GetName(index)} must be encrypted at rest, not stored as plaintext.");
        }
    }

    private static byte[] CreateKeyMaterial()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
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
