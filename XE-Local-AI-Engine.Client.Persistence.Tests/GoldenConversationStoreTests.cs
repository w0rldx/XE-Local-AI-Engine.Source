namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class GoldenConversationStoreTests : IDisposable
{
    private const string Instructions = "You are a careful engineering agent. Follow the repository conventions exactly.";
    private const string InputTurns = """[{"role":"user","text":"Summarize the change."}]""";
    private const string Assertion = """{"requiredPhrases":["summary"],"forbiddenPhrases":["TODO"]}""";
    private const string Rubric = "Judge whether the answer is a faithful, concise summary.";
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    [Test]
    public async Task AddAsync_ThenReadBackInNewContext_DecryptsInputTurnsAssertionAndRubric()
    {
        var databasePath = GetDatabasePath("roundtrip.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid goldenId;
        Guid agentId;
        await using (var writeContext = CreateContext(databasePath, keyHolder))
        {
            await writeContext.Database.EnsureDeletedAsync();
            await writeContext.Database.EnsureCreatedAsync();

            agentId = await SeedAgentAsync(writeContext);
            var store = new GoldenConversationStore(writeContext, TimeProvider.System);
            var added = await store.AddAsync(CreateInput(agentId));

            AssertEx.Equal(agentId, added.AgentDefinitionId);
            AssertEx.Equal("Summary case", added.Title);
            AssertEx.Equal(InputTurns, added.InputTurns);
            AssertEx.Equal(Assertion, added.Assertion);
            AssertEx.Equal(Rubric, added.Rubric);
            AssertEx.True(added.Enabled, "The seeded case is enabled.");
            AssertEx.True(added.Id != Guid.Empty, "Add should assign a golden id.");
            AssertEx.True(added.CreatedAtUtc > 0, "Add should stamp a creation time.");
            AssertEx.Equal(added.CreatedAtUtc, added.UpdatedAtUtc);
            goldenId = added.Id;
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new GoldenConversationStore(readContext, TimeProvider.System);

        var byId = AssertEx.NotNull(await readStore.GetByIdAsync(goldenId), "Golden case should be found by id.");
        AssertEx.Equal(InputTurns, byId.InputTurns);
        AssertEx.Equal(Assertion, byId.Assertion);
        AssertEx.Equal(Rubric, byId.Rubric);

        var list = await readStore.ListByAgentAsync(agentId);
        AssertEx.Equal(1, list.Count);

        var unknown = await readStore.GetByIdAsync(Guid.NewGuid());
        AssertEx.Null(unknown, "Unknown id should return null.");
    }

    [Test]
    public async Task AddAsync_WithNullAssertionAndRubric_RoundTripsNull()
    {
        var databasePath = GetDatabasePath("null-optional.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid goldenId;
        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var agentId = await SeedAgentAsync(context);
            var store = new GoldenConversationStore(context, TimeProvider.System);
            var added = await store.AddAsync(CreateInput(agentId) with { Assertion = null, Rubric = null });
            goldenId = added.Id;
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new GoldenConversationStore(readContext, TimeProvider.System);

        var record = AssertEx.NotNull(await readStore.GetByIdAsync(goldenId), "Golden case should be found by id.");
        AssertEx.Null(record.Assertion, "A null assertion should round-trip as null.");
        AssertEx.Null(record.Rubric, "A null rubric should round-trip as null.");
        AssertEx.Equal(InputTurns, record.InputTurns);
    }

    [Test]
    public async Task ListEnabledByAgentAsync_ReturnsOnlyEnabledOrderedByCreatedAt()
    {
        var databasePath = GetDatabasePath("list-enabled.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var clock = new MutableTimeProvider(1_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var agentId = await SeedAgentAsync(context);
        var otherAgentId = await SeedAgentAsync(context);
        var store = new GoldenConversationStore(context, clock);

        clock.Advance(1);
        var firstEnabled = await store.AddAsync(CreateInput(agentId) with { Title = "first" });
        clock.Advance(1);
        var secondEnabled = await store.AddAsync(CreateInput(agentId) with { Title = "second" });
        // A disabled case on the same agent must be excluded.
        _ = await store.AddAsync(CreateInput(agentId) with { Title = "parked", Enabled = false });
        // An enabled case on a different agent must be excluded.
        _ = await store.AddAsync(CreateInput(otherAgentId) with { Title = "other-agent" });

        var enabled = await store.ListEnabledByAgentAsync(agentId);

        AssertEx.Equal(2, enabled.Count);
        AssertEx.Equal("first", enabled[0].Title);
        AssertEx.Equal("second", enabled[1].Title);
        AssertEx.Equal(firstEnabled.Id, enabled[0].Id);
        AssertEx.Equal(secondEnabled.Id, enabled[1].Id);
    }

    [Test]
    public async Task ListByAgentAsync_IsScopedToTheOwningAgent()
    {
        var databasePath = GetDatabasePath("list-scoped.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var agentId = await SeedAgentAsync(context);
        var otherAgentId = await SeedAgentAsync(context);
        var store = new GoldenConversationStore(context, TimeProvider.System);

        _ = await store.AddAsync(CreateInput(agentId));
        _ = await store.AddAsync(CreateInput(agentId) with { Enabled = false });
        _ = await store.AddAsync(CreateInput(otherAgentId));

        var owned = await store.ListByAgentAsync(agentId);

        AssertEx.Equal(2, owned.Count);
    }

    [Test]
    public async Task DeleteAsync_RemovesRow()
    {
        var databasePath = GetDatabasePath("delete.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var agentId = await SeedAgentAsync(context);
        var store = new GoldenConversationStore(context, TimeProvider.System);

        var added = await store.AddAsync(CreateInput(agentId));

        AssertEx.True(await store.DeleteAsync(added.Id), "Delete should report a removed row.");
        AssertEx.Null(await store.GetByIdAsync(added.Id), "Deleted golden case should no longer be found.");
        AssertEx.False(await store.DeleteAsync(added.Id), "Deleting a missing id should report no removal.");
    }

    [Test]
    public async Task DatabaseFile_AfterAdd_DoesNotContainPlaintextFreeText()
    {
        var databasePath = GetDatabasePath("ciphertext.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var input = "SECRET-INPUT-" + Guid.NewGuid().ToString("N");
        var assertion = "SECRET-ASSERTION-" + Guid.NewGuid().ToString("N");
        var rubric = "SECRET-RUBRIC-" + Guid.NewGuid().ToString("N");

        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var agentId = await SeedAgentAsync(context);
            var store = new GoldenConversationStore(context, TimeProvider.System);
            _ = await store.AddAsync(CreateInput(agentId) with { InputTurns = input, Assertion = assertion, Rubric = rubric });
        }

        var fileBytes = await File.ReadAllBytesAsync(databasePath);
        AssertEx.False(ContainsSubsequence(fileBytes, Encoding.UTF8.GetBytes(input)),
            "The SQLite file should not contain the plaintext input turns.");
        AssertEx.False(ContainsSubsequence(fileBytes, Encoding.UTF8.GetBytes(assertion)),
            "The SQLite file should not contain the plaintext assertion.");
        AssertEx.False(ContainsSubsequence(fileBytes, Encoding.UTF8.GetBytes(rubric)),
            "The SQLite file should not contain the plaintext rubric.");
    }

    private static async Task<Guid> SeedAgentAsync(NodeChatDbContext context)
    {
        var store = new AgentDefinitionStore(context, TimeProvider.System);
        var agent = await store.AddAsync(new AgentDefinitionInput(
            "Builder",
            Description: null,
            Instructions,
            ModelProfile: null,
            ReasoningEffort: null,
            AgentDefinitionKind.Single,
            AllowedToolNames: [],
            ToolApprovals: new Dictionary<string, bool>(),
            OrchestrationTopologyJson: null));
        return agent.Id;
    }

    private static GoldenConversationInput CreateInput(Guid agentDefinitionId)
    {
        return new GoldenConversationInput(
            agentDefinitionId,
            Title: "Summary case",
            InputTurns,
            Assertion,
            Rubric,
            Enabled: true);
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

    private static byte[] CreateKeyMaterial()
    {
        return Enumerable.Range(0, 32).Select(static value => (byte)(value + 1)).ToArray();
    }

    private static bool ContainsSubsequence(byte[] source, byte[] needle)
    {
        if (needle.Length == 0)
        {
            return true;
        }

        for (var sourceIndex = 0; sourceIndex <= source.Length - needle.Length; sourceIndex++)
        {
            var matched = true;
            for (var needleIndex = 0; needleIndex < needle.Length; needleIndex++)
            {
                if (source[sourceIndex + needleIndex] == needle[needleIndex])
                {
                    continue;
                }

                matched = false;
                break;
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
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
