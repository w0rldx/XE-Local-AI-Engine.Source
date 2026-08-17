namespace XE_Local_AI_Engine.Client.Persistence.Tests.Training;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Covers both interceptor directions for <c>training_runs</c>. A column registered for encryption but not for
///     decryption (or the reverse) round-trips as garbage rather than failing loudly, so all six encrypted columns are
///     asserted here as ciphertext at rest AND plaintext after a fresh read.
/// </summary>
public sealed class TrainingRunEncryptionTests : IDisposable
{
    private const string FreezeJson = """{"schemaVersion":1,"sampleIds":["a"],"holdout":["b"],"blobSha256":"ab"}""";
    private const string OptionsJson = """{"schemaVersion":1,"epochs":3,"learningRate":0.0002}""";
    private const string LicenseConfirmationJson = """{"license":"apache-2.0","confirmedAtUtc":1,"confirmedBy":"operator"}""";
    private const string ProgressJson = """{"step":12,"totalSteps":100,"loss":0.42}""";
    private const string LogTail = "trainer: step 12/100 loss=0.42\n";
    private const string LaunchReceiptJson = """{"pid":4242,"pgid":4242,"executable":"/opt/uv/python","startTimeTicks":7,"runToken":"t"}""";
    private const string QualityDecisionJson = """{"schemaVersion":1,"policyVersion":1,"outcome":"Passed"}""";

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task RunPayloads_WhenSavedAndReadBack_RoundTripThroughBothInterceptors()
    {
        var databasePath = GetDatabasePath("run-roundtrip.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await SeedAsync(databasePath, keyHolder);

        await using var readContext = AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
        var run = AssertEx.NotNull(await readContext.TrainingRuns.SingleAsync(), "Run should be readable.");
        AssertEx.Equal(FreezeJson, Encoding.UTF8.GetString(run.FreezeJson));
        AssertEx.Equal(OptionsJson, Encoding.UTF8.GetString(run.OptionsJson));
        AssertEx.Equal(LicenseConfirmationJson, Encoding.UTF8.GetString(run.LicenseConfirmationJson!));
        AssertEx.Equal(ProgressJson, Encoding.UTF8.GetString(run.ProgressJson!));
        AssertEx.Equal(LogTail, Encoding.UTF8.GetString(run.LogTail!));
        AssertEx.Equal(LaunchReceiptJson, Encoding.UTF8.GetString(run.LaunchReceiptJson!));
    }

    [Test]
    public async Task RunPayloads_WhenPersisted_AreCiphertextAtRest()
    {
        var databasePath = GetDatabasePath("run-at-rest.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await SeedAsync(databasePath, keyHolder);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();

        // One literal statement — CA2100 rejects a composed command text, so the columns cannot be looped over.
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT freeze_json, options_json, license_confirmation_json, progress_json, log_tail, launch_receipt_json FROM training_runs LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync();
        AssertEx.True(await reader.ReadAsync(), "Expected a seeded run to inspect.");

        var plaintexts = new[]
        {
            FreezeJson,
            OptionsJson,
            LicenseConfirmationJson,
            ProgressJson,
            LogTail,
            LaunchReceiptJson
        };
        for (var index = 0; index < plaintexts.Length; index++)
        {
            var stored = reader.GetValue(index) as byte[] ?? throw new AssertionException($"Expected a non-null BLOB in {reader.GetName(index)}.");
            AssertEx.False(stored.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(plaintexts[index])),
                $"{reader.GetName(index)} must be encrypted at rest, not stored as plaintext.");
        }
    }

    [Test]
    public async Task ArtifactQualityDecision_IsEncryptedAtRestAndRoundTrips()
    {
        var databasePath = GetDatabasePath("quality-at-rest.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        await SeedAsync(databasePath, keyHolder);

        await using (var readContext = AgentDefinitionTestContextFactory.Create(databasePath, keyHolder))
        {
            var artifact = AssertEx.NotNull(await readContext.TrainingArtifacts.SingleAsync());
            AssertEx.Equal(QualityDecisionJson, Encoding.UTF8.GetString(artifact.QualityDecisionJson!));
        }

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT quality_decision_json FROM training_artifacts LIMIT 1;";
        var stored = (byte[])(await command.ExecuteScalarAsync())!;
        AssertEx.False(stored.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(QualityDecisionJson)));
    }

    [Test]
    public async Task RunCiphertext_WhenMovedToAnotherRunRow_FailsToDecrypt()
    {
        var databasePath = GetDatabasePath("run-swap.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await SeedAsync(databasePath, keyHolder);

        // The AAD binds each column to its own run id, so a writer cannot copy one run's freeze onto another row and
        // have it read back as that run's frozen membership.
        await using (var connection = new SqliteConnection($"Data Source={databasePath};Foreign Keys=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE training_runs SET id = $id;";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            AssertEx.Equal(expected: 1, await command.ExecuteNonQueryAsync());
        }

        await using var readContext = AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
        _ = await AssertEx.ThrowsAsync<CryptographicException>(async () => _ = await readContext.TrainingRuns.SingleAsync(),
            "Run ciphertext read under another run id must fail the AEAD tag check.");
    }

    private static async Task SeedAsync(string databasePath, INodeSqliteKeyHolder keyHolder)
    {
        await using var context = AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
        _ = await context.Database.EnsureDeletedAsync();
        _ = await context.Database.EnsureCreatedAsync();

        var definition = new TrainingDatasetDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Tool calling v1",
            Kind = TrainingDatasetKind.ToolCalling,
            DefinitionJson = Encoding.UTF8.GetBytes("""{"schemaVersion":1}"""),
            DefinitionVersion = 1,
            Version = 1,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1
        };
        var dataset = new TrainingDataset
        {
            Id = Guid.NewGuid(),
            DefinitionId = definition.Id,
            DefinitionVersion = 1,
            Name = "Primary",
            Status = TrainingDatasetStatus.Ready,
            Revision = 2,
            ContentFingerprint = "v1:" + new string('a', count: 64),
            Version = 1,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1
        };
        var baseArtifact = new TrainingBaseArtifact
        {
            Id = Guid.NewGuid(),
            RepoId = "org/base-model",
            Revision = new string('b', count: 40),
            Status = TrainingBaseArtifactStatus.Ready,
            FilesJson = Encoding.UTF8.GetBytes("""[]"""),
            TotalBytes = 42,
            Version = 1,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1
        };
        var run = new TrainingRun
        {
            Id = Guid.NewGuid(),
            DatasetId = dataset.Id,
            DatasetContentFingerprint = dataset.ContentFingerprint,
            DatasetRevision = dataset.Revision,
            FreezeJson = Encoding.UTF8.GetBytes(FreezeJson),
            BaseArtifactId = baseArtifact.Id,
            OptionsJson = Encoding.UTF8.GetBytes(OptionsJson),
            LicenseConfirmationJson = Encoding.UTF8.GetBytes(LicenseConfirmationJson),
            Status = TrainingRunStatus.Training,
            ProgressJson = Encoding.UTF8.GetBytes(ProgressJson),
            LogTail = Encoding.UTF8.GetBytes(LogTail),
            LaunchReceiptJson = Encoding.UTF8.GetBytes(LaunchReceiptJson),
            Version = 1,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1
        };
        var artifact = new TrainingArtifact
        {
            Id = Guid.NewGuid(),
            RunId = run.Id,
            Kind = TrainingArtifactKind.MergedGguf,
            Path = "staged.gguf",
            Sha256 = new string('c', count: 64),
            SizeBytes = 1,
            SmokeState = TrainingArtifactSmokeState.Passed,
            QualityComparisonId = Guid.NewGuid(),
            QualityDecisionJson = Encoding.UTF8.GetBytes(QualityDecisionJson),
            Version = 1,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1
        };

        _ = context.TrainingDatasetDefinitions.Add(definition);
        _ = context.TrainingDatasets.Add(dataset);
        _ = context.TrainingBaseArtifacts.Add(baseArtifact);
        _ = context.TrainingRuns.Add(run);
        _ = context.TrainingArtifacts.Add(artifact);
        _ = context.TrainingWorkItems.Add(new TrainingWorkItem
        {
            Kind = TrainingWorkKind.TrainingRun,
            TargetId = run.Id,
            Status = TrainingWorkStatus.Running,
            Attempt = 1,
            Version = 1,
            EnqueuedAtUtc = 1
        });
        _ = await context.SaveChangesAsync();

        // The save-changes interceptor restores plaintext onto the tracked graph after the flush, so the in-memory
        // entity never observes ciphertext.
        AssertEx.Equal(FreezeJson, Encoding.UTF8.GetString(run.FreezeJson));
    }

    private static byte[] CreateKeyMaterial()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    private string GetDatabasePath(string fileName)
    {
        _ = Directory.CreateDirectory(_rootPath);
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
