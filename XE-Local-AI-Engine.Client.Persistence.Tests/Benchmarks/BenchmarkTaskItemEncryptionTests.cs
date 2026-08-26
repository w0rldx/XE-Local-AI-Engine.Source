namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Both interceptor directions for the four encrypted task-item payloads. A column registered for encryption but
///     not for decryption (or the reverse) round-trips as garbage rather than failing loudly, so each is asserted as
///     ciphertext at rest AND as plaintext after a fresh read — and the per-column AAD is proven by substitution.
///     <para>
///         A verifier config carries expected answers and a generator config carries the parameters that produce them,
///         which is why neither is plaintext and why neither may be presentable as the prompt.
///     </para>
/// </summary>
public sealed class BenchmarkTaskItemEncryptionTests : IDisposable
{
    private const string PromptJson = """{"prompt":"Sort this list."}""";
    private const string ReferenceJson = """{"answer":"[1,2,3]"}""";
    private const string VerifierJson = """{"correctness":{"expected":"[1,2,3]"}}""";
    private const string GeneratorJson = """{"contextTokens":[8192],"needleDepthPercent":[50]}""";

    private static readonly Guid ProjectId = new("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid FirstItemId = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid SecondItemId = new("bbbbbbbb-0000-0000-0000-000000000003");

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task TaskItemPayloads_WhenSavedAndReadBack_RoundTripThroughBothInterceptors()
    {
        var databasePath = GetDatabasePath("task-item-roundtrip.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await SeedAsync(databasePath, keyHolder).ConfigureAwait(false);

        await using var readContext = AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
        var first = await readContext.BenchmarkTaskItems.SingleAsync(entity => entity.Id == FirstItemId).ConfigureAwait(false);
        AssertEx.Equal(PromptJson, Encoding.UTF8.GetString(first.PromptJson));
        AssertEx.Equal(ReferenceJson, Encoding.UTF8.GetString(first.ReferenceAnswerJson!));
        AssertEx.Equal(VerifierJson, Encoding.UTF8.GetString(first.VerifierConfigJson!));
        AssertEx.Equal(GeneratorJson, Encoding.UTF8.GetString(first.GeneratorConfigJson!));

        // A plain prompt item carries none of the three optional payloads; the optional path must store and read NULL
        // rather than dereference a missing value.
        var second = await readContext.BenchmarkTaskItems.SingleAsync(entity => entity.Id == SecondItemId).ConfigureAwait(false);
        AssertEx.Null(second.ReferenceAnswerJson, "An item with no reference answer stays NULL.");
        AssertEx.Null(second.VerifierConfigJson, "An item with no verifier override stays NULL.");
        AssertEx.Null(second.GeneratorConfigJson, "A prompt item has no generator parameters.");
    }

    [Test]
    public async Task TaskItemPayloads_WhenPersisted_AreCiphertextAtRest()
    {
        var databasePath = GetDatabasePath("task-item-at-rest.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await SeedAsync(databasePath, keyHolder).ConfigureAwait(false);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);

        // One literal statement — CA2100 rejects a composed command text, so the columns cannot be looped over.
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT prompt_json, reference_answer_json, verifier_config_json, generator_config_json FROM benchmark_task_items WHERE id = $id;";
        command.Parameters.AddWithValue("$id", FirstItemId);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        AssertEx.True(await reader.ReadAsync().ConfigureAwait(false), "Expected a seeded item to inspect.");

        var plaintexts = new[]
        {
            PromptJson,
            ReferenceJson,
            VerifierJson,
            GeneratorJson
        };
        for (var index = 0; index < plaintexts.Length; index++)
        {
            var stored = reader.GetValue(index) as byte[] ?? throw new AssertionException($"Expected a non-null BLOB in {reader.GetName(index)}.");
            AssertCiphertext(stored, plaintexts[index], reader.GetName(index));
        }
    }

    /// <summary>The plaintext identity columns stay readable without a key — that is what lets the ranking read scan them.</summary>
    [Test]
    public async Task TaskItemInputHashAndKind_StayPlaintext()
    {
        var databasePath = GetDatabasePath("task-item-plaintext.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await SeedAsync(databasePath, keyHolder).ConfigureAwait(false);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind || '|' || input_hash FROM benchmark_task_items WHERE id = $id;";
        command.Parameters.AddWithValue("$id", FirstItemId);
        AssertEx.Equal("prompt|v1:" + new string('c', count: 64), (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
    }

    [Test]
    public async Task TaskItemCiphertext_WhenMovedToAnotherItemRow_FailsToDecrypt()
    {
        var databasePath = GetDatabasePath("task-item-row-swap.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        await SeedAsync(databasePath, keyHolder).ConfigureAwait(false);

        await ExecuteAsync(databasePath,
            "UPDATE benchmark_task_items SET prompt_json = (SELECT prompt_json FROM benchmark_task_items WHERE id = $first) WHERE id = $second;",
            command =>
            {
                command.Parameters.AddWithValue("$first", FirstItemId);
                command.Parameters.AddWithValue("$second", SecondItemId);
            }).ConfigureAwait(false);

        await using var readContext = AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
        _ = await AssertEx.ThrowsAsync<CryptographicException>(async () => _ = await readContext.BenchmarkTaskItems.SingleAsync(entity => entity.Id == SecondItemId),
                              "One item's prompt must not read back as another item's.")
                          .ConfigureAwait(false);
    }

    [Test]
    public async Task TaskItemCiphertext_WhenMovedToAnotherColumn_FailsToDecrypt()
    {
        var databasePath = GetDatabasePath("task-item-column-swap.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        await SeedAsync(databasePath, keyHolder).ConfigureAwait(false);

        // Same row, same key — only the AAD column name differs, which is exactly what must stop a verifier config
        // (expected answers) from being served as the prompt.
        await ExecuteAsync(databasePath,
            "UPDATE benchmark_task_items SET prompt_json = verifier_config_json WHERE id = $id;",
            command => command.Parameters.AddWithValue("$id", FirstItemId)).ConfigureAwait(false);

        await using var readContext = AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
        _ = await AssertEx.ThrowsAsync<CryptographicException>(async () => _ = await readContext.BenchmarkTaskItems.SingleAsync(entity => entity.Id == FirstItemId),
                              "A verifier config presented as a prompt must fail the AEAD tag check.")
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
            Version = 1,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1
        });
        _ = context.BenchmarkTaskItems.Add(new BenchmarkTaskItem
        {
            Id = FirstItemId,
            ProjectId = ProjectId,
            Index = 0,
            Kind = "prompt",
            Revision = 1,
            InputHash = "v1:" + new string('c', count: 64),
            PromptJson = Encoding.UTF8.GetBytes(PromptJson),
            ReferenceAnswerJson = Encoding.UTF8.GetBytes(ReferenceJson),
            VerifierConfigJson = Encoding.UTF8.GetBytes(VerifierJson),
            GeneratorConfigJson = Encoding.UTF8.GetBytes(GeneratorJson),
            Version = 1,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1
        });
        _ = context.BenchmarkTaskItems.Add(new BenchmarkTaskItem
        {
            Id = SecondItemId,
            ProjectId = ProjectId,
            Index = 1,
            Kind = "prompt",
            Revision = 1,
            InputHash = "v1:" + new string('d', count: 64),
            PromptJson = Encoding.UTF8.GetBytes("""{"prompt":"And this one."}"""),
            Version = 1,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1
        });
        _ = await context.SaveChangesAsync().ConfigureAwait(false);

        // The save-changes interceptor restores plaintext onto the tracked graph after the flush, so the in-memory
        // entity never observes ciphertext.
        AssertEx.Equal(PromptJson, Encoding.UTF8.GetString(context.BenchmarkTaskItems.Local.Single(entity => entity.Id == FirstItemId).PromptJson));
    }

    private static async Task ExecuteAsync(string databasePath, string sql, Action<SqliteCommand> configure)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
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
            _key = null;
        }
    }
}
