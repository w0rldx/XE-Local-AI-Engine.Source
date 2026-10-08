namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     The node-key check value: recorded on a database's first run, then a key that does not match it stops startup
///     instead of booting into a node whose every encrypted read fails.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class NodeKeyCheckTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "xe-node-key-check-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task VerifyOrRecord_UnderTheSameKey_PassesOnEveryLaterStart()
    {
        var databasePath = await MigratedDatabaseAsync();
        using var key = new OtherKeyHolder(fill: 7);

        await using (var first = CreateContext(databasePath, key))
        {
            await first.VerifyOrRecordNodeKeyCheckAsync(CancellationToken.None);
        }

        await using var second = CreateContext(databasePath, key);
        await second.VerifyOrRecordNodeKeyCheckAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, await second.MaintenanceState.CountAsync(state => state.Name == NodeChatDbContext.NodeKeyCheckStateName));
    }

    [Test]
    public async Task VerifyOrRecord_UnderADifferentKey_Throws()
    {
        var databasePath = await MigratedDatabaseAsync();
        using var originalKey = new NullNodeSqliteKeyHolder();
        await using (var original = CreateContext(databasePath, originalKey))
        {
            await original.VerifyOrRecordNodeKeyCheckAsync(CancellationToken.None);
        }

        using var otherKey = new OtherKeyHolder(fill: 7);
        await using var mismatched = CreateContext(databasePath, otherKey);

        var exception = await AssertEx.ThrowsAsync<NodeKeyCustodyException>(() => mismatched.VerifyOrRecordNodeKeyCheckAsync(CancellationToken.None));

        AssertEx.True(exception.Message.Contains("does not match", StringComparison.Ordinal), exception.Message);
        AssertEx.True(exception.Message.Contains("move node.sqlite aside", StringComparison.Ordinal), exception.Message);
    }

    [Test]
    public async Task VerifyOrRecord_UnderADifferentKey_NamesTheDatabaseFileItReallyOpened()
    {
        var databasePath = await MigratedDatabaseAsync("renamed-node.sqlite");
        using var originalKey = new NullNodeSqliteKeyHolder();
        await using (var original = CreateContext(databasePath, originalKey))
        {
            await original.VerifyOrRecordNodeKeyCheckAsync(CancellationToken.None);
        }

        using var otherKey = new OtherKeyHolder(fill: 7);
        await using var mismatched = CreateContext(databasePath, otherKey);

        var exception = await AssertEx.ThrowsAsync<NodeKeyCustodyException>(() => mismatched.VerifyOrRecordNodeKeyCheckAsync(CancellationToken.None));

        AssertEx.True(exception.Message.Contains("move renamed-node.sqlite aside", StringComparison.Ordinal), exception.Message);
    }

    [Test]
    public async Task VerifyOrRecord_OnAnUpgradedDatabaseWithoutAMarker_AuthenticatesTheKeyBeforeBindingIt()
    {
        // An install that predates the check: encrypted rows under key A, no marker yet.
        var databasePath = await MigratedDatabaseAsync();
        using var keyA = new OtherKeyHolder(fill: 7);
        await using (var seed = CreateContext(databasePath, keyA))
        {
            seed.Conversations.Add(new NodeConversation
            {
                ConversationId = Guid.NewGuid(),
                Title = "an existing title"u8.ToArray()
            });
            await seed.SaveChangesAsync();
        }

        using var keyB = new OtherKeyHolder(fill: 9);
        await using (var wrong = CreateContext(databasePath, keyB))
        {
            _ = await AssertEx.ThrowsAsync<NodeKeyCustodyException>(() => wrong.VerifyOrRecordNodeKeyCheckAsync(CancellationToken.None));
            AssertEx.Equal(expected: 0,
                await wrong.MaintenanceState.CountAsync(state => state.Name == NodeChatDbContext.NodeKeyCheckStateName),
                "A wrong key must not be bound on the first start, or the right key would be refused afterwards.");
        }

        await using (var right = CreateContext(databasePath, keyA))
        {
            await right.VerifyOrRecordNodeKeyCheckAsync(CancellationToken.None);
        }

        await using var again = CreateContext(databasePath, keyA);
        await again.VerifyOrRecordNodeKeyCheckAsync(CancellationToken.None);
        AssertEx.Equal(expected: 1, await again.MaintenanceState.CountAsync(state => state.Name == NodeChatDbContext.NodeKeyCheckStateName));
    }

    [Test]
    public async Task VerifyOrRecord_WhenTheOnlyCiphertextIsAnAgentDefinition_RefusesAWrongKey()
    {
        var databasePath = await MigratedDatabaseAsync();
        using var keyA = new OtherKeyHolder(fill: 7);
        await using (var seed = CreateContext(databasePath, keyA))
        {
            _ = await new AgentDefinitionStore(seed, TimeProvider.System).AddAsync(AgentInput());
        }

        using var keyB = new OtherKeyHolder(fill: 9);
        await using var wrong = CreateContext(databasePath, keyB);

        _ = await AssertEx.ThrowsAsync<NodeKeyCustodyException>(() => wrong.VerifyOrRecordNodeKeyCheckAsync(CancellationToken.None));
        AssertEx.Equal(expected: 0, await wrong.MaintenanceState.CountAsync(state => state.Name == NodeChatDbContext.NodeKeyCheckStateName));
    }

    [Test]
    public async Task VerifyOrRecord_WhenOneTitleIsCorruptButAnAgentDecrypts_AcceptsTheRightKey()
    {
        var databasePath = await MigratedDatabaseAsync();
        using var keyA = new OtherKeyHolder(fill: 7);
        var conversationId = Guid.NewGuid();
        await using (var seed = CreateContext(databasePath, keyA))
        {
            seed.Conversations.Add(new NodeConversation
            {
                ConversationId = conversationId,
                Title = "soon corrupt"u8.ToArray()
            });
            await seed.SaveChangesAsync();
            _ = await new AgentDefinitionStore(seed, TimeProvider.System).AddAsync(AgentInput());
            AssertEx.Equal(expected: 1, await seed.Database.ExecuteSqlRawAsync("UPDATE conversations SET title = zeroblob(48);"));
        }

        await using (var right = CreateContext(databasePath, keyA))
        {
            await right.VerifyOrRecordNodeKeyCheckAsync(CancellationToken.None);
        }

        await using var again = CreateContext(databasePath, keyA);
        await again.VerifyOrRecordNodeKeyCheckAsync(CancellationToken.None);
        AssertEx.Equal(expected: 1, await again.MaintenanceState.CountAsync(state => state.Name == NodeChatDbContext.NodeKeyCheckStateName));
    }

    private static AgentDefinitionInput AgentInput()
    {
        return new AgentDefinitionInput
        {
            Name = "Probe",
            Description = "An encrypted description.",
            Instructions = "Encrypted instructions.",
            ModelProfile = null,
            ReasoningEffort = null,
            Kind = AgentDefinitionKind.Single,
            AllowedToolNames = [],
            ToolApprovals = new Dictionary<string, bool>(),
            OrchestrationTopologyJson = null
        };
    }

    private async Task<string> MigratedDatabaseAsync(string fileName = "node.sqlite")
    {
        Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, fileName);
        await MigratedDatabaseTemplate.CopyChatHeadAsync(databasePath);
        return databasePath;
    }

    private static NodeChatDbContext CreateContext(string databasePath, INodeSqliteKeyHolder keyHolder)
    {
        var options = new DbContextOptionsBuilder<NodeChatDbContext>()
                      .UseSqlite($"Data Source={databasePath}")
                      .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                      .AddInterceptors(new NodeEncryptionSaveChangesInterceptor(), new NodeEncryptionMaterializationInterceptor())
                      .Options;

        return new NodeChatDbContext(options, keyHolder);
    }

    private sealed class OtherKeyHolder : INodeSqliteKeyHolder
    {
        private readonly byte[] _key;

        public OtherKeyHolder(byte fill)
        {
            _key = Enumerable.Repeat(fill, 32).ToArray();
        }

        public ReadOnlyMemory<byte> Key => _key;

        public void Dispose()
        {
        }
    }
}
