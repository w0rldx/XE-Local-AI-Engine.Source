namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class SlashCommandStoreTests : IDisposable
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
    public async Task AddAsync_RoundTripsTypedActionAndEncryptsSensitiveColumns()
    {
        var path = GetPath("roundtrip.sqlite");
        using var key = new FixedKeyHolder();
        Guid id;
        await using (var context = AgentDefinitionTestContextFactory.Create(path, key))
        {
            await context.Database.EnsureCreatedAsync();
            var record = await new SlashCommandStore(context, TimeProvider.System)
                .AddAsync(new SlashCommandInput("review", "secret description", SlashCommandActionType.SendPrompt, "secret prompt"));
            id = record.Id;
            AssertEx.Equal("secret prompt", record.Prompt);
        }

        await using (var context = AgentDefinitionTestContextFactory.Create(path, key))
        {
            var record = AssertEx.NotNull(await new SlashCommandStore(context, TimeProvider.System).GetByIdAsync(id));
            AssertEx.Equal("secret description", record.Description);
            AssertEx.Equal(SlashCommandActionType.SendPrompt, record.ActionType);
            AssertEx.Equal("secret prompt", record.Prompt);
        }

        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT description, action_configuration FROM slash_commands WHERE id = $id";
        _ = command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync();
        AssertEx.True(await reader.ReadAsync());
        AssertEx.False(((byte[])reader.GetValue(0)).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("secret description")));
        var storedAction = (byte[])reader.GetValue(1);
        AssertEx.False(Encoding.UTF8.GetString(storedAction).Contains("secret prompt", StringComparison.Ordinal));
        var plaintextAction = NodePayloadProtector.Decrypt(storedAction, key.Key.Span, Guid.Empty, id, SlashCommand.ActionConfigurationColumnName("review"));
        AssertEx.Equal("{\"type\":\"sendPrompt\",\"version\":1,\"prompt\":\"secret prompt\"}", Encoding.UTF8.GetString(plaintextAction));
    }

    [Test]
    public async Task AddAsync_AtCapacity_RejectsOneHundredAndFirstCommand()
    {
        var path = GetPath("capacity.sqlite");
        using var key = new FixedKeyHolder();
        await using var context = AgentDefinitionTestContextFactory.Create(path, key);
        await context.Database.EnsureCreatedAsync();
        var store = new SlashCommandStore(context, TimeProvider.System);
        for (var index = 0; index < 100; index++)
        {
            _ = await store.AddAsync(new SlashCommandInput($"command-{index}", null, SlashCommandActionType.SendPrompt, "prompt"));
        }

        await AssertEx.ThrowsAsync<SlashCommandCapacityException>(() =>
            store.AddAsync(new SlashCommandInput("overflow", null, SlashCommandActionType.SendPrompt, "prompt")));
        AssertEx.Equal(expected: 100, (await store.ListAsync()).Count);
    }

    [Test]
    [Arguments("not-json")]
    [Arguments("{\"type\":\"sendPrompt\",\"version\":1,\"prompt\":\"ok\",\"extra\":true}")]
    [Arguments("{\"type\":\"sendPrompt\",\"type\":\"sendPrompt\",\"version\":1,\"prompt\":\"ok\"}")]
    [Arguments("{\"type\":\"sendPrompt\",\"version\":2,\"prompt\":\"ok\"}")]
    [Arguments("{\"type\":\"other\",\"version\":1,\"prompt\":\"ok\"}")]
    [Arguments("{\"type\":\"sendPrompt\",\"version\":\"1\",\"prompt\":\"ok\"}")]
    [Arguments("{\"type\":\"sendPrompt\",\"version\":1}")]
    public async Task GetByIdAsync_WithInvalidEnvelope_FailsClosedWithoutPayload(string actionJson)
    {
        var path = GetPath($"invalid-{Guid.NewGuid():N}.sqlite");
        using var key = new FixedKeyHolder();
        var id = Guid.NewGuid();
        await using (var context = AgentDefinitionTestContextFactory.Create(path, key))
        {
            await context.Database.EnsureCreatedAsync();
            _ = context.SlashCommands.Add(new SlashCommand
            {
                Id = id,
                Name = "invalid",
                ActionType = (int)SlashCommandActionType.SendPrompt,
                ActionConfiguration = Encoding.UTF8.GetBytes(actionJson),
                CreatedAtUtc = 1,
                UpdatedAtUtc = 1
            });
            _ = await context.SaveChangesAsync();
        }

        await using var readContext = AgentDefinitionTestContextFactory.Create(path, key);
        var exception = await AssertEx.ThrowsAsync<InvalidDataException>(() => new SlashCommandStore(readContext, TimeProvider.System).GetByIdAsync(id));
        AssertEx.False(exception.Message.Contains(actionJson, StringComparison.Ordinal), "The invalid payload must not be reflected in the exception message.");
    }

    [Test]
    public async Task GetByIdAsync_WithActionTypeMismatch_FailsClosed()
    {
        var path = GetPath("invalid-action-type.sqlite");
        using var key = new FixedKeyHolder();
        var id = Guid.NewGuid();
        await using (var context = AgentDefinitionTestContextFactory.Create(path, key))
        {
            await context.Database.EnsureCreatedAsync();
            _ = context.SlashCommands.Add(new SlashCommand
            {
                Id = id,
                Name = "invalid",
                ActionType = 999,
                ActionConfiguration = Encoding.UTF8.GetBytes("{\"type\":\"sendPrompt\",\"version\":1,\"prompt\":\"ok\"}"),
                CreatedAtUtc = 1,
                UpdatedAtUtc = 1
            });
            _ = await context.SaveChangesAsync();
        }

        await using var readContext = AgentDefinitionTestContextFactory.Create(path, key);
        _ = await AssertEx.ThrowsAsync<InvalidDataException>(() => new SlashCommandStore(readContext, TimeProvider.System).GetByIdAsync(id));
    }

    [Test]
    public async Task AddAsync_FromTwoContextsAtNinetyNine_AllowsExactlyOneReservation()
    {
        var path = GetPath("capacity-race.sqlite");
        using var key = new FixedKeyHolder();
        await using (var seedContext = AgentDefinitionTestContextFactory.Create(path, key))
        {
            await seedContext.Database.EnsureCreatedAsync();
            var seedStore = new SlashCommandStore(seedContext, TimeProvider.System);
            for (var index = 0; index < 99; index++)
            {
                _ = await seedStore.AddAsync(new SlashCommandInput($"seed-{index}", null, SlashCommandActionType.SendPrompt, "prompt"));
            }
        }

        await using var firstContext = AgentDefinitionTestContextFactory.Create(path, key);
        await using var secondContext = AgentDefinitionTestContextFactory.Create(path, key);
        var outcomes = await Task.WhenAll(TryAddAsync(new SlashCommandStore(firstContext, TimeProvider.System), "racer-a"),
            TryAddAsync(new SlashCommandStore(secondContext, TimeProvider.System), "racer-b"));

        AssertEx.Equal(expected: 1, outcomes.Count(static succeeded => succeeded));
        AssertEx.Equal(expected: 1, outcomes.Count(static succeeded => !succeeded));
        await using var verifyContext = AgentDefinitionTestContextFactory.Create(path, key);
        AssertEx.Equal(expected: 100, (await new SlashCommandStore(verifyContext, TimeProvider.System).ListAsync()).Count);
    }

    [Test]
    [Arguments("ping")]
    [Arguments("shadow")]
    public async Task GetByIdAsync_WhenPlaintextNameIsTampered_FailsAuthentication(string tamperedName)
    {
        var path = GetPath($"tampered-{tamperedName}.sqlite");
        using var key = new FixedKeyHolder();
        Guid id;
        await using (var context = AgentDefinitionTestContextFactory.Create(path, key))
        {
            await context.Database.EnsureCreatedAsync();
            id = (await new SlashCommandStore(context, TimeProvider.System)
                .AddAsync(new SlashCommandInput("review", "description", SlashCommandActionType.SendPrompt, "prompt"))).Id;
        }

        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE slash_commands SET name = $name WHERE id = $id";
            _ = command.Parameters.AddWithValue("$name", tamperedName);
            _ = command.Parameters.AddWithValue("$id", id);
            AssertEx.Equal(expected: 1, await command.ExecuteNonQueryAsync());
        }

        await using var readContext = AgentDefinitionTestContextFactory.Create(path, key);
        _ = await AssertEx.ThrowsAsync<CryptographicException>(() => new SlashCommandStore(readContext, TimeProvider.System).GetByIdAsync(id));
    }

    [Test]
    [Arguments("ping")]
    [Arguments("bad--name")]
    public async Task GetByIdAsync_WhenPersistedNameIsInvalid_FailsClosed(string invalidName)
    {
        var path = GetPath($"invalid-name-{Guid.NewGuid():N}.sqlite");
        using var key = new FixedKeyHolder();
        var id = Guid.NewGuid();
        await using (var context = AgentDefinitionTestContextFactory.Create(path, key))
        {
            await context.Database.EnsureCreatedAsync();
            _ = context.SlashCommands.Add(new SlashCommand
            {
                Id = id,
                Name = invalidName,
                ActionType = (int)SlashCommandActionType.SendPrompt,
                ActionConfiguration = Encoding.UTF8.GetBytes("{\"type\":\"sendPrompt\",\"version\":1,\"prompt\":\"ok\"}"),
                CreatedAtUtc = 1,
                UpdatedAtUtc = 1
            });
            _ = await context.SaveChangesAsync();
        }

        await using var readContext = AgentDefinitionTestContextFactory.Create(path, key);
        _ = await AssertEx.ThrowsAsync<InvalidDataException>(() => new SlashCommandStore(readContext, TimeProvider.System).GetByIdAsync(id));
    }

    private static async Task<bool> TryAddAsync(SlashCommandStore store, string name)
    {
        try
        {
            _ = await store.AddAsync(new SlashCommandInput(name, null, SlashCommandActionType.SendPrompt, "prompt"));
            return true;
        }
        catch (SlashCommandCapacityException)
        {
            return false;
        }
    }

    private string GetPath(string name)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, name);
    }

    private sealed class FixedKeyHolder : INodeSqliteKeyHolder
    {
        private byte[]? _key = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
        public ReadOnlyMemory<byte> Key => _key ?? throw new ObjectDisposedException(nameof(FixedKeyHolder));

        public void Dispose()
        {
            if (_key is not null)
            {
                CryptographicOperations.ZeroMemory(_key);
            }

            _key = null;
        }
    }
}
