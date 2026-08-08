namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Behavioral round-trips for the conversation <c>memory_excluded</c> (temporary-chat) flag added by the adaptive-
///     memory feature: a new conversation inherits its bound agent's <c>DefaultTemporaryChat</c>, and the per-conversation
///     flag round-trips through the raw-SQL read path. The flag suppresses post-run extraction WRITE only — these tests
///     assert persistence/read; the write-suppression behavior lands with the extraction seam (follow-up).
/// </summary>
public sealed class ConversationMemoryExcludedTests : IDisposable
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
    public async Task CreateConversation_WhenBoundAgentDefaultsTemporary_InheritsMemoryExcludedTrue()
    {
        await using var provider = await BuildProviderAsync("inherit-temp.sqlite").ConfigureAwait(false);
        var agentId = await SeedAgentAsync(provider, defaultTemporaryChat: true).ConfigureAwait(false);
        var service = CreateService(provider);

        var created = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Temp chat", "node", CreatedAtUtc: 10, AgentDefinitionId: agentId)).ConfigureAwait(false);

        AssertEx.True(created.MemoryExcluded, "A new conversation bound to a default-temporary agent should inherit MemoryExcluded=true.");

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(created.ConversationId).ConfigureAwait(false), "Conversation should be readable.");
        AssertEx.True(loaded.MemoryExcluded, "The inherited flag should round-trip through the read path.");
    }

    [Test]
    public async Task CreateConversation_WhenBoundAgentNotTemporary_InheritsMemoryExcludedFalse()
    {
        await using var provider = await BuildProviderAsync("inherit-non-temp.sqlite").ConfigureAwait(false);
        var agentId = await SeedAgentAsync(provider, defaultTemporaryChat: false).ConfigureAwait(false);
        var service = CreateService(provider);

        var created = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Normal chat", "node", CreatedAtUtc: 10, AgentDefinitionId: agentId)).ConfigureAwait(false);

        AssertEx.False(created.MemoryExcluded, "A new conversation bound to a non-temporary agent should default to MemoryExcluded=false.");
    }

    [Test]
    public async Task CreateConversation_WhenUnbound_DefaultsMemoryExcludedFalse()
    {
        await using var provider = await BuildProviderAsync("unbound-default.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);

        var created = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Unbound chat", "node", CreatedAtUtc: 10)).ConfigureAwait(false);

        AssertEx.False(created.MemoryExcluded, "An unbound conversation defaults to MemoryExcluded=false.");
    }

    [Test]
    public async Task ConversationMemoryExcluded_Toggle_RoundTrips()
    {
        await using var provider = await BuildProviderAsync("toggle-roundtrip.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);

        var created = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Toggle chat", "node", CreatedAtUtc: 10)).ConfigureAwait(false);
        AssertEx.False(created.MemoryExcluded, "A fresh unbound conversation starts non-temporary.");

        // The per-conversation override PATCH endpoint is a follow-up; here the column write is simulated directly so
        // the read path is proven to carry the flag. The read seam is what the extraction service will consume.
        await SetMemoryExcludedAsync(provider, created.ConversationId, excluded: true).ConfigureAwait(false);

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(created.ConversationId).ConfigureAwait(false), "Conversation should be readable.");
        AssertEx.True(loaded.MemoryExcluded, "Toggling memory_excluded to true should round-trip through the read path.");
    }

    private static async Task<Guid> SeedAgentAsync(ServiceProvider provider, bool defaultTemporaryChat)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        // The agent_definitions table is created by EnsureCreatedAsync (it is an EF entity); the AgentDefinitionStore
        // owns the typed write, including the new DefaultTemporaryChat flag.
        var store = new AgentDefinitionStore(dbContext, TimeProvider.System);
        var agent = await store.AddAsync(new AgentDefinitionInput("Builder",
                                   Description: null,
                                   "You are a careful engineering agent.",
                                   ModelProfile: null,
                                   ReasoningEffort: null,
                                   AgentDefinitionKind.Single,
                                   [],
                                   new Dictionary<string, bool>(),
                                   OrchestrationTopologyJson: null,
                                   DefaultTemporaryChat: defaultTemporaryChat))
                               .ConfigureAwait(false);
        return agent.Id;
    }

    private static async Task SetMemoryExcludedAsync(ServiceProvider provider, Guid conversationId, bool excluded)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync("UPDATE conversations SET memory_excluded = {0} WHERE conversation_id = {1};",
            excluded ? 1 : 0,
            conversationId).ConfigureAwait(false);
    }

    private async Task<ServiceProvider> BuildProviderAsync(string fileName)
    {
        var databasePath = GetDatabasePath(fileName);
        var services = new ServiceCollection();
        services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<NodeChatPersistenceWriter>();

        var provider = services.BuildServiceProvider(true);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.EnsureDeletedAsync().ConfigureAwait(false);
        await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);

        return provider;
    }

    private static NodeChatPersistenceService CreateService(ServiceProvider provider)
    {
        return new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
