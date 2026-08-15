namespace XE_Local_AI_Engine.Client.Persistence.Tests.Training;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Covers both interceptor directions for the three encrypted columns Slice 5 adds. A column registered for
///     encryption but not for decryption (or the reverse) round-trips as garbage rather than failing loudly, so each is
///     asserted as ciphertext at rest AND as plaintext after a fresh read.
/// </summary>
public sealed class TrainingEvaluationEncryptionTests : IDisposable
{
    private const string MembershipJson = """{"schemaVersion":1,"holdoutSampleIds":["6f9619ff-8b86-d011-b42d-00c04fc964ff"]}""";
    private const string ResultsJson = """{"schemaVersion":1,"entries":[{"sampleId":"6f9619ff-8b86-d011-b42d-00c04fc964ff","passed":true}]}""";
    private const string DeltasJson = """{"schemaVersion":1,"baseAccuracy":0.5,"tunedAccuracy":0.75}""";
    private const string BaseModelName = "base-model";
    private const string TunedModelName = "tuned-model";

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task EvaluationPayloads_WhenSavedAndReadBack_RoundTripThroughBothInterceptors()
    {
        var databasePath = GetDatabasePath("evaluation-roundtrip.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await SeedAsync(databasePath, keyHolder);

        await using var readContext = AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
        var evaluation = AssertEx.NotNull(await readContext.TrainingEvaluationRuns.SingleAsync(item => item.ModelName == BaseModelName),
            "Evaluation should be readable.");
        AssertEx.Equal(MembershipJson, Encoding.UTF8.GetString(evaluation.MembershipJson));
        AssertEx.Equal(ResultsJson, Encoding.UTF8.GetString(evaluation.ResultsJson!));

        var report = AssertEx.NotNull(await readContext.TrainingComparisonReports.SingleAsync(), "Report should be readable.");
        AssertEx.Equal(DeltasJson, Encoding.UTF8.GetString(report.DeltasJson));
    }

    [Test]
    public async Task EvaluationPayloads_WhenPersisted_AreCiphertextAtRest()
    {
        var databasePath = GetDatabasePath("evaluation-at-rest.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await SeedAsync(databasePath, keyHolder);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();

        // One literal statement per table — CA2100 rejects a composed command text, so the columns cannot be looped over.
        await using (var evaluationCommand = connection.CreateCommand())
        {
            evaluationCommand.CommandText = "SELECT membership_json, results_json FROM training_evaluation_runs WHERE model_name = 'base-model' LIMIT 1;";
            await using var reader = await evaluationCommand.ExecuteReaderAsync();
            AssertEx.True(await reader.ReadAsync(), "Expected a seeded evaluation to inspect.");
            AssertCiphertext(reader, index: 0, MembershipJson);
            AssertCiphertext(reader, index: 1, ResultsJson);
        }

        await using var reportCommand = connection.CreateCommand();
        reportCommand.CommandText = "SELECT deltas_json FROM training_comparison_reports LIMIT 1;";
        await using var reportReader = await reportCommand.ExecuteReaderAsync();
        AssertEx.True(await reportReader.ReadAsync(), "Expected a seeded report to inspect.");
        AssertCiphertext(reportReader, index: 0, DeltasJson);
    }

    [Test]
    public async Task EvaluationCiphertext_WhenMovedToAnotherRow_FailsToDecrypt()
    {
        var databasePath = GetDatabasePath("evaluation-swap.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await SeedAsync(databasePath, keyHolder);

        // The AAD binds each column to its own evaluation id, so a writer cannot copy one evaluation's membership onto
        // another row and have it read back as that evaluation's frozen hold-out set — which would silently make two
        // sides of a comparison answer different questions while looking comparable.
        // Foreign keys off for this rewrite only: the point is to model a raw database writer moving a row, and the
        // report's declared reference would otherwise refuse the move before the AEAD tag ever got a chance to.
        await using (var connection = new SqliteConnection($"Data Source={databasePath};Foreign Keys=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE training_evaluation_runs SET id = $id WHERE model_name = 'base-model';";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            AssertEx.Equal(expected: 1, await command.ExecuteNonQueryAsync());
        }

        await using var readContext = AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
        _ = await AssertEx.ThrowsAsync<CryptographicException>(
            async () => _ = await readContext.TrainingEvaluationRuns.SingleAsync(item => item.ModelName == BaseModelName),
            "Evaluation ciphertext read under another evaluation id must fail the AEAD tag check.");
    }

    private static void AssertCiphertext(SqliteDataReader reader, int index, string plaintext)
    {
        var stored = reader.GetValue(index) as byte[] ?? throw new AssertionException($"Expected a non-null BLOB in {reader.GetName(index)}.");
        AssertEx.False(stored.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(plaintext)),
            $"{reader.GetName(index)} must be encrypted at rest, not stored as plaintext.");
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
        var evaluation = new TrainingEvaluationRun
        {
            Id = Guid.NewGuid(),
            ModelName = BaseModelName,
            DatasetId = dataset.Id,
            DatasetContentFingerprint = dataset.ContentFingerprint,
            MembershipJson = Encoding.UTF8.GetBytes(MembershipJson),
            Status = TrainingEvaluationStatus.Succeeded,
            ResultsJson = Encoding.UTF8.GetBytes(ResultsJson),
            TotalCount = 1,
            ScoredCount = 1,
            PassedCount = 1,
            PerKindJson = """{"tool-call":{"total":1,"passed":1}}""",
            Version = 1,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1
        };
        // The report declares real foreign keys to both sides, so both have to exist. Every read and every rewrite
        // below therefore scopes itself to the base row by model name rather than assuming a single evaluation.
        var tuned = new TrainingEvaluationRun
        {
            Id = Guid.NewGuid(),
            ModelName = TunedModelName,
            DatasetId = dataset.Id,
            DatasetContentFingerprint = dataset.ContentFingerprint,
            MembershipJson = Encoding.UTF8.GetBytes(MembershipJson),
            Status = TrainingEvaluationStatus.Succeeded,
            TotalCount = 1,
            Version = 1,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1
        };
        var report = new TrainingComparisonReport
        {
            Id = Guid.NewGuid(),
            Name = "base vs tuned",
            BaseEvaluationRunId = evaluation.Id,
            TunedEvaluationRunId = tuned.Id,
            DeltasJson = Encoding.UTF8.GetBytes(DeltasJson),
            Version = 1,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1
        };

        _ = context.TrainingDatasetDefinitions.Add(definition);
        _ = context.TrainingDatasets.Add(dataset);
        _ = context.TrainingEvaluationRuns.Add(evaluation);
        _ = context.TrainingEvaluationRuns.Add(tuned);
        _ = context.TrainingComparisonReports.Add(report);
        _ = await context.SaveChangesAsync();

        // The save-changes interceptor restores plaintext onto the tracked graph after the flush, so the in-memory
        // entity never observes ciphertext.
        AssertEx.Equal(MembershipJson, Encoding.UTF8.GetString(evaluation.MembershipJson));
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
