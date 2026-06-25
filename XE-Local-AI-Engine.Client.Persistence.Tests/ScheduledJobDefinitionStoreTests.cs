namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed class ScheduledJobDefinitionStoreTests : IDisposable
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
    public async Task AddAsync_ThenGetById_RoundTripsAllFields()
    {
        var databasePath = GetDatabasePath("add-getbyid.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ScheduledJobDefinitionStore(context, TimeProvider.System);
        var input = CreateCronInput("tpl-a", "My Cron Job");
        var added = await store.AddAsync(input);

        AssertEx.True(added.Id != Guid.Empty, "Add should assign a non-empty Id.");
        AssertEx.Equal("tpl-a", added.TemplateId);
        AssertEx.Equal("My Cron Job", added.DisplayName);
        AssertEx.Equal("Runs every hour", added.Description);
        AssertEx.True(added.Enabled, "Definition should be enabled by default.");
        AssertEx.Equal(ScheduleKind.Cron, added.ScheduleKind);
        AssertEx.Equal("0 * * * *", added.CronExpression);
        AssertEx.Equal(SchedulerMisfirePolicy.Smart, added.MisfirePolicy);
        AssertEx.Equal(ScheduledJobCreator.User, added.CreatedBy);
        AssertEx.True(added.CreatedAtUtc > 0, "Add should stamp CreatedAtUtc.");
        AssertEx.Equal(added.CreatedAtUtc, added.UpdatedAtUtc);
        AssertEx.Null(added.DisabledAtUtc);
        AssertEx.Null(added.DeletedAtUtc);

        var byId = AssertEx.NotNull(await store.GetByIdAsync(added.Id), "Definition should be found by id.");
        AssertEx.Equal(added.Id, byId.Id);
        AssertEx.Equal(added.DisplayName, byId.DisplayName);
        AssertEx.Equal(added.CreatedAtUtc, byId.CreatedAtUtc);
    }

    [Test]
    public async Task GetByIdAsync_WhenIdUnknown_ReturnsNull()
    {
        var databasePath = GetDatabasePath("getbyid-unknown.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ScheduledJobDefinitionStore(context, TimeProvider.System);

        var result = await store.GetByIdAsync(Guid.NewGuid());

        AssertEx.Null(result, "Unknown id should return null.");
    }

    [Test]
    public async Task ListAsync_ExcludesSoftDeletedByDefault_IncludesWhenFlagSet()
    {
        var databasePath = GetDatabasePath("list-deleted.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ScheduledJobDefinitionStore(context, TimeProvider.System);

        var active = await store.AddAsync(CreateCronInput("tpl-active", "Active"));
        var toDelete = await store.AddAsync(CreateCronInput("tpl-delete", "ToDelete"));
        _ = await store.SoftDeleteAsync(toDelete.Id);

        var defaultList = await store.ListAsync();
        var includeDeleted = await store.ListAsync(true);

        AssertEx.Equal(expected: 1, defaultList.Count);
        AssertEx.Equal(active.Id, defaultList[0].Id);
        AssertEx.Equal(expected: 2, includeDeleted.Count);
    }

    [Test]
    public async Task ListByTemplateAsync_FiltersToTemplateAndExcludesSoftDeleted()
    {
        var databasePath = GetDatabasePath("list-by-template.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ScheduledJobDefinitionStore(context, TimeProvider.System);

        var a1 = await store.AddAsync(CreateCronInput("tpl-x", "X1"));
        var a2 = await store.AddAsync(CreateCronInput("tpl-x", "X2"));
        _ = await store.AddAsync(CreateCronInput("tpl-y", "Y1"));
        _ = await store.SoftDeleteAsync(a2.Id);

        var byTemplate = await store.ListByTemplateAsync("tpl-x");

        AssertEx.Equal(expected: 1, byTemplate.Count);
        AssertEx.Equal(a1.Id, byTemplate[0].Id);
    }

    [Test]
    public async Task ListEnabledAsync_ReturnsOnlyEnabledNonDeleted()
    {
        var databasePath = GetDatabasePath("list-enabled.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ScheduledJobDefinitionStore(context, TimeProvider.System);

        var enabled = await store.AddAsync(CreateCronInput("tpl-en", "Enabled"));
        _ = await store.AddAsync(CreateCronInput("tpl-dis", "Disabled") with
        {
            Enabled = false
        });
        var toDelete = await store.AddAsync(CreateCronInput("tpl-del", "Deleted"));
        _ = await store.SoftDeleteAsync(toDelete.Id);

        var result = await store.ListEnabledAsync();

        AssertEx.Equal(expected: 1, result.Count);
        AssertEx.Equal(enabled.Id, result[0].Id);
    }

    [Test]
    public async Task UpdateAsync_BumpsUpdatedAtUtcAndOverwritesFields()
    {
        var databasePath = GetDatabasePath("update.sqlite");
        using var keyHolder = CreateKeyHolder();
        var clock = new MutableTimeProvider(1_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ScheduledJobDefinitionStore(context, clock);
        var added = await store.AddAsync(CreateCronInput("tpl-upd", "Original Name"));

        clock.Advance(500);
        var updatedInput = CreateCronInput("tpl-upd", "Updated Name") with
        {
            Description = "Changed"
        };
        var updated = AssertEx.NotNull(await store.UpdateAsync(added.Id, updatedInput), "Update should return updated record.");

        AssertEx.Equal("Updated Name", updated.DisplayName);
        AssertEx.Equal("Changed", updated.Description);
        AssertEx.Equal(added.CreatedAtUtc + 500, updated.UpdatedAtUtc);
        AssertEx.True(updated.UpdatedAtUtc > added.UpdatedAtUtc, "Update should bump UpdatedAtUtc.");
        AssertEx.Equal(added.CreatedAtUtc, updated.CreatedAtUtc);
    }

    [Test]
    public async Task UpdateAsync_WhenIdUnknown_ReturnsNull()
    {
        var databasePath = GetDatabasePath("update-unknown.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ScheduledJobDefinitionStore(context, TimeProvider.System);

        var result = await store.UpdateAsync(Guid.NewGuid(), CreateCronInput("tpl", "X"));

        AssertEx.Null(result, "Update on unknown id should return null.");
    }

    [Test]
    public async Task SetEnabledAsync_WhenDisabling_StampsDisabledAtUtcAndBumpsUpdatedAt()
    {
        var databasePath = GetDatabasePath("set-enabled-disable.sqlite");
        using var keyHolder = CreateKeyHolder();
        var clock = new MutableTimeProvider(2_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ScheduledJobDefinitionStore(context, clock);
        var added = await store.AddAsync(CreateCronInput("tpl-se", "Job"));
        AssertEx.True(added.Enabled, "Starts enabled.");

        clock.Advance(300);
        var disabled = AssertEx.NotNull(await store.SetEnabledAsync(added.Id, enabled: false), "SetEnabled should return the updated record.");

        AssertEx.False(disabled.Enabled, "SetEnabled(false) should disable the definition.");
        AssertEx.True(disabled.DisabledAtUtc.HasValue, "DisabledAtUtc should be stamped when disabling.");
        AssertEx.Equal(added.CreatedAtUtc + 300, disabled.DisabledAtUtc!.Value);
        AssertEx.Equal(added.CreatedAtUtc + 300, disabled.UpdatedAtUtc);
    }

    [Test]
    public async Task SetEnabledAsync_WhenReEnabling_ClearsDisabledAtUtc()
    {
        var databasePath = GetDatabasePath("set-enabled-reenable.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ScheduledJobDefinitionStore(context, TimeProvider.System);
        var added = await store.AddAsync(CreateCronInput("tpl-re", "Job") with
        {
            Enabled = false
        });
        _ = await store.SetEnabledAsync(added.Id, enabled: false);

        var reEnabled = AssertEx.NotNull(await store.SetEnabledAsync(added.Id, enabled: true), "Re-enable should return the updated record.");

        AssertEx.True(reEnabled.Enabled, "Re-enabling should set Enabled=true.");
        AssertEx.Null(reEnabled.DisabledAtUtc, "DisabledAtUtc should be cleared on re-enable.");
    }

    [Test]
    public async Task SetEnabledAsync_WhenIdUnknown_ReturnsNull()
    {
        var databasePath = GetDatabasePath("set-enabled-unknown.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ScheduledJobDefinitionStore(context, TimeProvider.System);

        var result = await store.SetEnabledAsync(Guid.NewGuid(), enabled: false);

        AssertEx.Null(result, "SetEnabled on unknown id should return null.");
    }

    [Test]
    public async Task SoftDeleteAsync_StampsDeletedAtUtcAndExcludesFromDefaultList()
    {
        var databasePath = GetDatabasePath("soft-delete.sqlite");
        using var keyHolder = CreateKeyHolder();
        var clock = new MutableTimeProvider(5_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ScheduledJobDefinitionStore(context, clock);
        var added = await store.AddAsync(CreateCronInput("tpl-sd", "ToDelete"));

        clock.Advance(200);
        var deleted = await store.SoftDeleteAsync(added.Id);
        AssertEx.True(deleted, "SoftDelete should report a removed row.");

        var byId = AssertEx.NotNull(await store.GetByIdAsync(added.Id), "GetById should still return a soft-deleted row.");
        AssertEx.True(byId.DeletedAtUtc.HasValue, "DeletedAtUtc should be stamped.");
        AssertEx.False(byId.Enabled, "SoftDelete should also disable the definition.");

        var defaultList = await store.ListAsync();
        AssertEx.Equal(expected: 0, defaultList.Count);
    }

    [Test]
    public async Task SoftDeleteAsync_WhenIdUnknown_ReturnsFalse()
    {
        var databasePath = GetDatabasePath("soft-delete-unknown.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ScheduledJobDefinitionStore(context, TimeProvider.System);

        var result = await store.SoftDeleteAsync(Guid.NewGuid());

        AssertEx.False(result, "SoftDelete on unknown id should return false.");
    }

    [Test]
    public async Task AddAsync_WithParameterJson_DecryptsOnRead()
    {
        var databasePath = GetDatabasePath("param-json-decrypt.sqlite");
        using var keyHolder = CreateKeyHolder();
        const string paramJson = """{"maxItems":100,"tags":["etl","nightly"]}""";

        Guid defId;
        await using (var writeContext = CreateContext(databasePath, keyHolder))
        {
            await writeContext.Database.EnsureDeletedAsync();
            await writeContext.Database.EnsureCreatedAsync();

            var store = new ScheduledJobDefinitionStore(writeContext, TimeProvider.System);
            var added = await store.AddAsync(CreateCronInput("tpl-enc", "Encrypted") with
            {
                ParameterJson = paramJson
            });
            AssertEx.Equal(paramJson, added.ParameterJson);
            defId = added.Id;
        }

        // Read back in a fresh context to confirm decryption on materialization.
        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new ScheduledJobDefinitionStore(readContext, TimeProvider.System);

        var byId = AssertEx.NotNull(await readStore.GetByIdAsync(defId));
        AssertEx.Equal(paramJson, byId.ParameterJson);
    }

    [Test]
    public async Task AddAsync_WithParameterJson_StoredAsCiphertext()
    {
        var databasePath = GetDatabasePath("param-json-ciphertext.sqlite");
        using var keyHolder = CreateKeyHolder();
        var paramJson = "SECRET-PARAM-" + Guid.NewGuid().ToString("N");

        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();

            var store = new ScheduledJobDefinitionStore(context, TimeProvider.System);
            _ = await store.AddAsync(CreateCronInput("tpl-cipher", "Job") with
            {
                ParameterJson = paramJson
            });
        }

        var rawBytes = await ReadRawParameterJsonAsync(databasePath);
        var plaintextBytes = Encoding.UTF8.GetBytes(paramJson);

        AssertEx.True(rawBytes.Length > 0, "Encrypted column should have non-empty BLOB data.");
        AssertEx.False(rawBytes.AsSpan().SequenceEqual(plaintextBytes),
            "parameter_json column should be encrypted at rest, not plaintext.");
    }

    [Test]
    public async Task AddAsync_WithNullParameterJson_RoundTripsNull()
    {
        var databasePath = GetDatabasePath("param-json-null.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ScheduledJobDefinitionStore(context, TimeProvider.System);
        var added = await store.AddAsync(CreateCronInput("tpl-null", "Job") with
        {
            ParameterJson = null
        });

        AssertEx.Null(added.ParameterJson, "Null ParameterJson should round-trip as null.");
    }

    private static async Task<byte[]> ReadRawParameterJsonAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT parameter_json FROM scheduled_job_definitions LIMIT 1;";
        var value = await command.ExecuteScalarAsync();
        return value as byte[] ?? throw new AssertionException("Expected a non-null encrypted BLOB in parameter_json.");
    }

    private static NodeChatDbContext CreateContext(string databasePath, INodeSqliteKeyHolder keyHolder)
    {
        return AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    private static INodeSqliteKeyHolder CreateKeyHolder()
    {
        var key = Enumerable.Range(start: 0, count: 32).Select(static v => (byte)(v + 1)).ToArray();
        return new FixedNodeSqliteKeyHolder(key);
    }

    private static ScheduledJobDefinitionInput CreateCronInput(string templateId, string displayName)
    {
        return new ScheduledJobDefinitionInput(templateId,
            displayName,
            "Runs every hour",
            Enabled: true,
            ScheduleKind.Cron,
            "0 * * * *",
            IntervalSeconds: null,
            RepeatCount: null,
            StartAtUtc: null,
            EndAtUtc: null,
            "UTC",
            SchedulerMisfirePolicy.Smart,
            PreventOverlap: false,
            MaxRuntimeSeconds: null,
            ParameterJson: null,
            ScheduledJobCreator.User);
    }

    private sealed class MutableTimeProvider(long initialMilliseconds) : TimeProvider
    {
        private long _milliseconds = initialMilliseconds;

        public void Advance(long milliseconds)
        {
            _milliseconds += milliseconds;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(_milliseconds);
        }
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
