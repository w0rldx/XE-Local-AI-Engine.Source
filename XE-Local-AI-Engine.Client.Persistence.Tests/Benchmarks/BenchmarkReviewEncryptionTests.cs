namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Review notes may quote the answer, so they are ciphertext at rest under their own AAD column name, asserted in both
///     interceptor directions.
/// </summary>
/// <remarks>
///     A column registered for one direction only round-trips as garbage instead of failing loudly. The review shares
///     its row id with the run, so the column-swap test differs from a valid read in the AAD column name alone.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class BenchmarkReviewEncryptionTests : IDisposable
{
    private const string Notes = "The answer quotes the secret token 7f3a-review-notes.";
    private const string OutputParts = """[{"text":"the candidate answer"}]""";

    private static readonly Guid ProjectId = new("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid RunId = new("cccccccc-0000-0000-0000-000000000002");

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task ReviewNotes_WhenSavedAndReadBack_RoundTripThroughBothInterceptors()
    {
        var databasePath = GetDatabasePath("review-roundtrip.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        await SeedAsync(databasePath, keyHolder);

        await using var readContext = AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
        var review = await readContext.BenchmarkRunReviews.AsNoTracking().SingleAsync(entity => entity.RunId == RunId);

        AssertEx.Equal(Notes, Encoding.UTF8.GetString(AssertEx.NotNull(review.NotesJson)));
    }

    [Test]
    public async Task ReviewNotes_AreCiphertextAtRest()
    {
        var databasePath = GetDatabasePath("review-at-rest.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        await SeedAsync(databasePath, keyHolder);

        var fileBytes = await SqliteFileProbe.ReadAllBytesAsync(databasePath);
        AssertEx.False(fileBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Notes)) >= 0, "The SQLite file must not contain the plaintext notes.");

        // The scores beside the notes are plaintext on purpose: staleness and ranking compare them without a key.
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT notes_json, state || '|' || score || '|' || criteria_scores_json FROM benchmark_run_reviews WHERE run_id = $run;";
        command.Parameters.AddWithValue("$run", RunId);
        await using var reader = await command.ExecuteReaderAsync();
        AssertEx.True(await reader.ReadAsync(), "Expected the seeded review to inspect.");
        var stored = reader.GetValue(0) as byte[] ?? throw new AssertionException("Expected a non-null BLOB in notes_json.");
        AssertEx.False(Encoding.UTF8.GetString(stored).Contains("secret token", StringComparison.Ordinal), "notes_json ciphertext must not contain plaintext fragments.");
        AssertEx.Equal("""approved|80|[{"criterionId":"accuracy","score":8}]""", reader.GetString(1));
    }

    [Test]
    public async Task ReviewNotes_WhenAadColumnSwapped_FailToDecrypt()
    {
        var databasePath = GetDatabasePath("review-column-swap.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        await SeedAsync(databasePath, keyHolder);

        // Same key, same row id (the review's id IS the run's id here): only the AAD column name differs, which is exactly what must stop a run's
        // encrypted answer from being presented as review notes.
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE benchmark_run_reviews SET notes_json = (SELECT output_parts_json FROM benchmark_runs WHERE id = $run) WHERE id = $run;";
            command.Parameters.AddWithValue("$run", RunId);
            AssertEx.Equal(expected: 1, await command.ExecuteNonQueryAsync());
        }

        await using var readContext = AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
        _ = await AssertEx.ThrowsAsync<CryptographicException>(async () => _ = await readContext.BenchmarkRunReviews.AsNoTracking().SingleAsync(entity => entity.Id == RunId),
            "A ciphertext from another column must fail the AEAD tag check under the notes column's AAD.");
    }

    private static async Task SeedAsync(string databasePath, INodeSqliteKeyHolder keyHolder)
    {
        await using var context = AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
        _ = await context.Database.EnsureDeletedAsync();
        _ = await context.Database.EnsureCreatedAsync();

        _ = context.BenchmarkProjects.Add(new BenchmarkProject
        {
            Id = ProjectId,
            Name = "Benchmark",
            CoreTaskJson = Encoding.UTF8.GetBytes("""{"task":"answer"}"""),
            ContextTokens = 4096,
            AgentDefinitionId = Guid.NewGuid(),
            RequiresReview = true,
            Version = 1,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1
        });
        _ = context.BenchmarkRuns.Add(new BenchmarkRun
        {
            Id = RunId,
            ProjectId = ProjectId,
            RuntimeSnapshotJson = Encoding.UTF8.GetBytes("""{"schemaVersion":1}"""),
            PrimaryModelName = "model.gguf",
            ModelContentFingerprint = "v1:" + new string('a', 64),
            AgentName = "Agent",
            AgentVersion = 1,
            RequestedContextTokens = 4096,
            PrimaryStatus = BenchmarkPrimaryStatus.Succeeded,
            OutputPartsJson = Encoding.UTF8.GetBytes(OutputParts),
            CellKey = "run:" + RunId,
            TaskInputHash = "v1:legacy",
            TaskItemSetHash = "v1:legacy",
            UserScore = 80,
            Version = 2,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1
        });
        _ = context.BenchmarkRunReviews.Add(new BenchmarkRunReview
        {
            Id = RunId,
            RunId = RunId,
            State = "approved",
            Score = 80,
            CriteriaScoresJson = """[{"criterionId":"accuracy","score":8}]""",
            NotesJson = Encoding.UTF8.GetBytes(Notes),
            Version = 1,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1
        });
        _ = await context.SaveChangesAsync();

        // The save interceptor restores plaintext onto the tracked graph after the flush.
        AssertEx.Equal(Notes, Encoding.UTF8.GetString(AssertEx.NotNull(context.BenchmarkRunReviews.Local.Single().NotesJson)));
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

    private sealed class FixedNodeSqliteKeyHolder : INodeSqliteKeyHolder
    {
        private byte[]? _key;

        public FixedNodeSqliteKeyHolder(byte[] key)
        {
            _key = key;
        }

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
            _key = null;
        }
    }
}
