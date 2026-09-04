namespace XE_Local_AI_Engine.Client.Persistence.Tests.Integrations;

using System.Globalization;
using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     One throwaway on-disk SQLite database per test, with both encryption interceptors wired, so a round trip through
///     a fresh context proves the encrypt/decrypt path rather than the change tracker's in-memory plaintext. Shaped on
///     <c>DevWorkflowTestFixture</c>.
/// </summary>
/// <remarks>
///     A real file is not optional here: <c>IIntegrationExecutionStore.AcceptAsync</c> opens its <b>own</b>
///     <see cref="SqliteConnection" /> from the context's connection string and takes SQLite's write lock with
///     <c>BEGIN IMMEDIATE</c>, which an in-memory or shared-connection harness cannot reproduce.
/// </remarks>
internal sealed class IntegrationTestFixture : IDisposable
{
    private readonly NullNodeSqliteKeyHolder _keyHolder = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-integrations-" + Guid.NewGuid().ToString("N"));

    public string DatabasePath => Path.Combine(_root, "integrations.sqlite");

    public void Dispose()
    {
        _keyHolder.Dispose();
        SqliteFileProbe.ReleasePooledHandles();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    public NodeChatDbContext CreateContext() => AgentDefinitionTestContextFactory.Create(DatabasePath, _keyHolder);

    public async Task<NodeChatDbContext> CreateSchemaAsync()
    {
        var context = CreateContext();
        _ = await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
        return context;
    }

    public static IntegrationTrigger Trigger(string name = "sensor-ingest", Guid? id = null, Guid? agentDefinitionId = null) =>
        new()
        {
            Id = id ?? Guid.NewGuid(),
            Name = name,
            DisplayName = "Sensor ingest",
            Description = "Accepts a sensor reading and runs the triage agent.",
            Enabled = true,
            TargetKind = IntegrationTargetKind.Agent,
            TargetAgentDefinitionId = agentDefinitionId ?? Guid.NewGuid(),
            SessionPolicy = IntegrationSessionPolicy.PerInvocation,
            AcceptedInputKinds = IntegrationInputKinds.Text | IntegrationInputKinds.Json,
            CreatedAtUtc = 1_000,
            UpdatedAtUtc = 1_000,
            Version = 0
        };

    public static IntegrationApiKey ApiKey(string keyPrefix = "xeint_a1b2c3d4", Guid? id = null, Guid? principalId = null, byte[]? keyHash = null) =>
        new()
        {
            Id = id ?? Guid.NewGuid(),
            PrincipalId = principalId ?? Guid.NewGuid(),
            KeyPrefix = keyPrefix,
            KeyHash = keyHash ?? [1, 2, 3, 4, 5, 6, 7, 8],
            Label = "Greenhouse controller",
            AllowedTriggerIdsJson = null,
            CreatedAtUtc = 1_000
        };

    public static IntegrationSession Session(Guid triggerId, Guid principalId, Guid? id = null, Guid? conversationId = null, long lastActivityUtc = 2_000) =>
        new()
        {
            Id = id ?? Guid.NewGuid(),
            TriggerId = triggerId,
            PrincipalId = principalId,
            ConversationId = conversationId ?? Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            Status = IntegrationSessionStatus.Active,
            CreatedAtUtc = 2_000,
            LastActivityUtc = lastActivityUtc,
            ExecutionCount = 1,
            LastSequence = 1
        };

    public static IntegrationExecution Execution(Guid triggerId,
        Guid sessionId,
        Guid principalId,
        Guid? id = null,
        Guid? requestId = null,
        IntegrationExecutionStatus status = IntegrationExecutionStatus.Accepted,
        long receivedAtUtc = 3_000) =>
        new()
        {
            Id = id ?? Guid.NewGuid(),
            TriggerId = triggerId,
            SessionId = sessionId,
            PrincipalId = principalId,
            RequestId = requestId ?? Guid.NewGuid(),
            RequestFingerprint = new byte[32],
            KeyPrefix = "xeint_a1b2c3d4",
            InvocationId = Guid.NewGuid(),
            Status = status,
            ReceivedAtUtc = receivedAtUtc,
            LastSequence = 1,
            Version = 0
        };

    public static IntegrationExecutionEvent Event(Guid executionId, long sequence, string eventType, byte[]? detailJson = null, Guid? id = null) =>
        new()
        {
            Id = id ?? Guid.NewGuid(),
            ExecutionId = executionId,
            Sequence = sequence,
            EventType = eventType,
            DetailJson = detailJson,
            OccurredAtUtc = 4_000
        };

    /// <summary>Runs a scalar query straight against the file, for assertions the entity model would false-pass.</summary>
    public async Task<object?> RawScalarAsync(string sql, Action<SqliteCommand>? configure = null)
    {
        await using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The SQL is a fixed literal in the calling suite; every value binds through `configure`.
        command.CommandText = sql;
#pragma warning restore CA2100
        configure?.Invoke(command);
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is DBNull ? null : value;
    }

    public async Task RawExecuteAsync(string sql, Action<SqliteCommand>? configure = null)
    {
        await using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Same: fixed literal, bound parameters.
        command.CommandText = sql;
#pragma warning restore CA2100
        configure?.Invoke(command);
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<long> RawTableCountAsync(string table)
    {
        var count = await RawScalarAsync($"SELECT COUNT(*) FROM {table};").ConfigureAwait(false);
        return Convert.ToInt64(count, CultureInfo.InvariantCulture);
    }
}
