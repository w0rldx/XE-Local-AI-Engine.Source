namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Covers both interceptor directions for the five encrypted judge columns. A column registered for encryption but
///     not for decryption (or the reverse) round-trips as garbage rather than failing loudly, so each one is asserted
///     as ciphertext at rest AND as plaintext after a fresh read — and the AAD is proven by substitution, across rows,
///     across columns and across attempts.
/// </summary>
public sealed class BenchmarkJudgeEncryptionTests : IDisposable
{
    private const string PolicyJson = """{"schemaVersion":1,"rubric":{"criteria":[{"id":"correctness","weight":40}]}}""";
    private const string RuntimeJson = """{"schemaVersion":1,"contextTokens":2048,"kvCacheType":"q8_0"}""";
    private const string ResultJson = """{"schemaVersion":2,"summary":"good","score":73}""";
    private const string ReceiptJson = """{"pid":4242,"executableSha256":"ab"}""";
    private const string EnvironmentFactsJson = """{"os":"linux","arch":"x64"}""";
    private const string PolicyHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly Guid ProjectId = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid RunId = new("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid RevisionId = new("aaaaaaaa-0000-0000-0000-000000000003");
    private static readonly Guid FirstAttemptId = new("aaaaaaaa-0000-0000-0000-000000000004");
    private static readonly Guid SecondAttemptId = new("aaaaaaaa-0000-0000-0000-000000000005");

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task JudgePayloads_WhenSavedAndReadBack_RoundTripThroughBothInterceptors()
    {
        var databasePath = GetDatabasePath("judge-roundtrip.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await SeedAsync(databasePath, keyHolder).ConfigureAwait(false);

        await using var readContext = AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
        var revision = await readContext.BenchmarkJudgePolicyRevisions.SingleAsync().ConfigureAwait(false);
        AssertEx.Equal(PolicyJson, Encoding.UTF8.GetString(revision.PolicyJson));

        var first = await readContext.BenchmarkJudgeAttempts.SingleAsync(entity => entity.Id == FirstAttemptId).ConfigureAwait(false);
        AssertEx.Equal(RuntimeJson, Encoding.UTF8.GetString(first.JudgeRuntimeJson!));
        AssertEx.Equal(ResultJson, Encoding.UTF8.GetString(first.ResultJson!));
        AssertEx.Equal(ReceiptJson, Encoding.UTF8.GetString(first.LaunchReceiptJson!));
        AssertEx.Equal(EnvironmentFactsJson, Encoding.UTF8.GetString(first.EnvironmentFactsJson!));

        // The attempt inserted directly as Failed never resolved a runtime; the optional path must store and read NULL
        // rather than dereference a missing value.
        var second = await readContext.BenchmarkJudgeAttempts.SingleAsync(entity => entity.Id == SecondAttemptId).ConfigureAwait(false);
        AssertEx.Null(second.JudgeRuntimeJson, "An unresolved judge runtime stays NULL.");
        AssertEx.Null(second.ResultJson, "A failed attempt has no result.");
        AssertEx.Null(second.LaunchReceiptJson, "An attempt that never launched has no receipt.");
        AssertEx.Null(second.EnvironmentFactsJson, "An attempt that never launched has no environment capture.");
    }

    [Test]
    public async Task JudgePayloads_WhenPersisted_AreCiphertextAtRest()
    {
        var databasePath = GetDatabasePath("judge-at-rest.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await SeedAsync(databasePath, keyHolder).ConfigureAwait(false);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);

        await using (var policyCommand = connection.CreateCommand())
        {
            policyCommand.CommandText = "SELECT policy_json FROM benchmark_judge_policy_revisions LIMIT 1;";
            AssertCiphertext((byte[])(await policyCommand.ExecuteScalarAsync().ConfigureAwait(false))!, PolicyJson, "policy_json");
        }

        // One literal statement — CA2100 rejects a composed command text, so the columns cannot be looped over.
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT judge_runtime_json, result_json, launch_receipt_json, environment_facts_json FROM benchmark_judge_attempts WHERE id = $id;";
        command.Parameters.AddWithValue("$id", FirstAttemptId);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        AssertEx.True(await reader.ReadAsync().ConfigureAwait(false), "Expected a seeded attempt to inspect.");

        var plaintexts = new[]
        {
            RuntimeJson,
            ResultJson,
            ReceiptJson,
            EnvironmentFactsJson
        };
        for (var index = 0; index < plaintexts.Length; index++)
        {
            var stored = reader.GetValue(index) as byte[] ?? throw new AssertionException($"Expected a non-null BLOB in {reader.GetName(index)}.");
            AssertCiphertext(stored, plaintexts[index], reader.GetName(index));
        }
    }

    [Test]
    public async Task PolicyCiphertext_WhenMovedToAnotherRevisionRow_FailsToDecrypt()
    {
        var databasePath = GetDatabasePath("judge-policy-swap.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        await SeedAsync(databasePath, keyHolder).ConfigureAwait(false);

        await ExecuteAsync(databasePath, "UPDATE benchmark_judge_policy_revisions SET id = $id;", command => command.Parameters.AddWithValue("$id", Guid.NewGuid()))
            .ConfigureAwait(false);

        await using var readContext = AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
        _ = await AssertEx.ThrowsAsync<CryptographicException>(async () => _ = await readContext.BenchmarkJudgePolicyRevisions.SingleAsync(),
                              "A policy read under another revision id must fail the AEAD tag check.")
                          .ConfigureAwait(false);
    }

    [Test]
    public async Task AttemptCiphertext_WhenMovedToAnotherAttemptRow_FailsToDecrypt()
    {
        var databasePath = GetDatabasePath("judge-attempt-swap.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        await SeedAsync(databasePath, keyHolder).ConfigureAwait(false);

        // Cross-attempt substitution: the second attempt now carries the first attempt's result ciphertext.
        await ExecuteAsync(databasePath,
            "UPDATE benchmark_judge_attempts SET result_json = (SELECT result_json FROM benchmark_judge_attempts WHERE id = $first) WHERE id = $second;",
            command =>
            {
                command.Parameters.AddWithValue("$first", FirstAttemptId);
                command.Parameters.AddWithValue("$second", SecondAttemptId);
            }).ConfigureAwait(false);

        await using var readContext = AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
        _ = await AssertEx.ThrowsAsync<CryptographicException>(async () => _ = await readContext.BenchmarkJudgeAttempts.SingleAsync(entity => entity.Id == SecondAttemptId),
                              "One attempt's result must not read back as another attempt's.")
                          .ConfigureAwait(false);
    }

    [Test]
    public async Task AttemptCiphertext_WhenMovedToAnotherColumn_FailsToDecrypt()
    {
        var databasePath = GetDatabasePath("judge-column-swap.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        await SeedAsync(databasePath, keyHolder).ConfigureAwait(false);

        // Same row, same key — only the AAD column name differs, which is exactly what must stop this.
        await ExecuteAsync(databasePath,
            "UPDATE benchmark_judge_attempts SET launch_receipt_json = result_json WHERE id = $id;",
            command => command.Parameters.AddWithValue("$id", FirstAttemptId)).ConfigureAwait(false);

        await using var readContext = AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
        _ = await AssertEx.ThrowsAsync<CryptographicException>(async () => _ = await readContext.BenchmarkJudgeAttempts.SingleAsync(entity => entity.Id == FirstAttemptId),
                              "A judge result presented as a launch receipt must fail the AEAD tag check.")
                          .ConfigureAwait(false);
    }

    private static async Task SeedAsync(string databasePath, INodeSqliteKeyHolder keyHolder)
    {
        await using var context = AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
        _ = await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        _ = await context.Database.EnsureCreatedAsync().ConfigureAwait(false);

        _ = context.BenchmarkProjects.Add(new BenchmarkProject
        {
            Id = ProjectId,
            Name = "Benchmark",
            CoreTaskJson = Encoding.UTF8.GetBytes("""{"task":"answer"}"""),
            ContextTokens = 4096,
            AgentDefinitionId = Guid.NewGuid(),
            CurrentJudgePolicyRevisionId = RevisionId,
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
            ModelContentFingerprint = "v1:" + new string('a', count: 64),
            AgentName = "Agent",
            AgentVersion = 1,
            RequestedContextTokens = 4096,
            PrimaryStatus = BenchmarkPrimaryStatus.Succeeded,
            OutputPartsJson = Encoding.UTF8.GetBytes("[]"),
            CurrentJudgeAttemptId = FirstAttemptId,
            Version = 1,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1
        });
        _ = context.BenchmarkJudgePolicyRevisions.Add(new BenchmarkJudgePolicyRevision
        {
            Id = RevisionId,
            ProjectId = ProjectId,
            Revision = 1,
            PolicyJson = Encoding.UTF8.GetBytes(PolicyJson),
            PolicyHash = PolicyHash,
            CohortGeneration = 1,
            CreatedAtUtc = 1
        });
        _ = context.BenchmarkJudgeAttempts.Add(new BenchmarkJudgeAttempt
        {
            Id = FirstAttemptId,
            RunId = RunId,
            Sequence = 1,
            PolicyRevisionId = RevisionId,
            CohortGeneration = 1,
            JudgeRuntimeJson = Encoding.UTF8.GetBytes(RuntimeJson),
            JudgeExecutionKey = new string('b', count: 64),
            Status = BenchmarkJudgeAttemptStatus.Succeeded,
            ResultJson = Encoding.UTF8.GetBytes(ResultJson),
            Score = 73,
            LaunchReceiptJson = Encoding.UTF8.GetBytes(ReceiptJson),
            EnvironmentFactsJson = Encoding.UTF8.GetBytes(EnvironmentFactsJson),
            EnqueuedAtUtc = 1,
            CompletedAtUtc = 2,
            Version = 1
        });
        _ = context.BenchmarkJudgeAttempts.Add(new BenchmarkJudgeAttempt
        {
            Id = SecondAttemptId,
            RunId = RunId,
            Sequence = 2,
            PolicyRevisionId = RevisionId,
            CohortGeneration = 1,
            Status = BenchmarkJudgeAttemptStatus.Failed,
            ErrorMessage = "judge runtime unresolved",
            EnqueuedAtUtc = 3,
            CompletedAtUtc = 3,
            Version = 1
        });
        _ = await context.SaveChangesAsync().ConfigureAwait(false);

        // The save-changes interceptor restores plaintext onto the tracked graph after the flush, so the in-memory
        // entity never observes ciphertext.
        AssertEx.Equal(PolicyJson, Encoding.UTF8.GetString(context.BenchmarkJudgePolicyRevisions.Local.Single().PolicyJson));
    }

    private static async Task ExecuteAsync(string databasePath, string sql, Action<SqliteCommand> configure)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);

        // Substituting ciphertext deliberately breaks referential integrity; the AEAD tag is what must catch it.
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            _ = await pragma.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Fixed literals from this suite; every value is a bound parameter.
        command.CommandText = sql;
#pragma warning restore CA2100
        configure(command);
        AssertEx.Equal(expected: 1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
    }

    private static void AssertCiphertext(byte[] stored, string plaintext, string columnName)
    {
        AssertEx.False(stored.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(plaintext)),
            $"{columnName} must be encrypted at rest, not stored as plaintext.");
        AssertEx.False(Encoding.UTF8.GetString(stored).Contains(plaintext, StringComparison.Ordinal),
            $"{columnName} ciphertext must not contain plaintext fragments.");
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
