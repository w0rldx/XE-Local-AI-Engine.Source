namespace XE_Local_AI_Engine.Tests.BackgroundServices;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Memory;
using XE_Local_AI_Engine.Client.Services.Memory.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Tests for <see cref="AgentExecutionLogRetentionService" />: a periodic sweep, on its own DI scope, deletes
///     execution-log rows older than the configured retention window and no-ops cleanly when disabled.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AgentExecutionLogRetentionServiceTests : IDisposable
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
    public async Task ExecuteAsync_SweepsRowsOlderThanRetentionWindow_KeepsRecent()
    {
        var agentId = Guid.NewGuid();
        await using var provider = await BuildProviderAsync("retention-sweep.sqlite");

        // A clearly-expired row (epoch 0 = far older than RetentionDays) and a fresh one (now).
        Guid freshLogId;
        await using (var seedScope = provider.CreateAsyncScope())
        {
            _ = await SeedRowAsync(seedScope.ServiceProvider, agentId, createdAtUtc: 0L);
            freshLogId = await SeedRowAsync(seedScope.ServiceProvider, agentId, NowMs());
        }

        using var service = CreateService(provider, new AgentExecutionLogRetentionOptions
        {
            Enabled = true,
            RetentionDays = 30,
            SweepInterval = TimeSpan.FromMilliseconds(50)
        });

        await service.StartAsync(CancellationToken.None);
        await AssertEx.EventuallyAsync(() =>
            {
                using var scope = provider.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IAgentExecutionLogStore>();
                return store.ListByAgentAsync(agentId, limit: 10).GetAwaiter().GetResult().Count == 1;
            },
            TimeSpan.FromSeconds(5),
            "The retention sweep should delete the expired row and keep the fresh one.");
        await service.StopAsync(CancellationToken.None);

        using var verifyScope = provider.CreateScope();
        var verifyStore = verifyScope.ServiceProvider.GetRequiredService<IAgentExecutionLogStore>();
        var remaining = await verifyStore.ListByAgentAsync(agentId, limit: 10);
        AssertEx.Equal(expected: 1, remaining.Count);
        AssertEx.Equal(freshLogId, remaining[0].Id);
    }

    [Test]
    public async Task ExecuteAsync_SweepsOnceAtStartup_BeforeTheFirstInterval()
    {
        var agentId = Guid.NewGuid();
        await using var provider = await BuildProviderAsync("retention-startup-sweep.sqlite");

        await using (var seedScope = provider.CreateAsyncScope())
        {
            // An expired row present at startup.
            _ = await SeedRowAsync(seedScope.ServiceProvider, agentId, createdAtUtc: 0L);
        }

        // A one-hour interval the test never waits out: any deletion must come from the startup sweep, not a periodic tick.
        using var service = CreateService(provider, new AgentExecutionLogRetentionOptions
        {
            Enabled = true,
            RetentionDays = 30,
            SweepInterval = TimeSpan.FromHours(1)
        });

        await service.StartAsync(CancellationToken.None);
        await AssertEx.EventuallyAsync(() =>
            {
                using var scope = provider.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IAgentExecutionLogStore>();
                return store.ListByAgentAsync(agentId, limit: 10).GetAwaiter().GetResult().Count == 0;
            },
            TimeSpan.FromSeconds(5),
            "The startup sweep should delete the expired row before the first periodic interval.");
        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task ExecuteAsync_WhenDisabled_DoesNotSweep()
    {
        var agentId = Guid.NewGuid();
        await using var provider = await BuildProviderAsync("retention-disabled.sqlite");

        await using (var seedScope = provider.CreateAsyncScope())
        {
            // An expired row that a disabled sweep must leave untouched.
            _ = await SeedRowAsync(seedScope.ServiceProvider, agentId, createdAtUtc: 0L);
        }

        using var service = CreateService(provider, new AgentExecutionLogRetentionOptions
        {
            Enabled = false,
            SweepInterval = TimeSpan.FromMilliseconds(50)
        });

        await service.StartAsync(CancellationToken.None);
        // A disabled sweeper returns from ExecuteAsync immediately without arming the timer. Await that completion
        // signal deterministically (rather than sleeping) to prove the background loop ran to completion — so any
        // regression that swept while disabled would have already run before the row-count assertion below.
        if (service.ExecuteTask is { } executeTask)
        {
            await executeTask.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await service.StopAsync(CancellationToken.None);

        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentExecutionLogStore>();
        var rows = await store.ListByAgentAsync(agentId, limit: 10);
        AssertEx.Equal(expected: 1, rows.Count);
    }

    private static AgentExecutionLogRetentionService CreateService(IServiceProvider provider, AgentExecutionLogRetentionOptions options)
    {
        return new AgentExecutionLogRetentionService(provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            Options.Create(options),
            NullLogger<AgentExecutionLogRetentionService>.Instance);
    }

    private static async Task<Guid> SeedRowAsync(IServiceProvider scopeProvider, Guid agentId, long createdAtUtc)
    {
        // The store stamps CreatedAtUtc from its clock, so seed each row through a store wired to a fixed clock — this
        // makes the row's age controllable without touching raw SQL.
        var dbContext = scopeProvider.GetRequiredService<NodeChatDbContext>();
        var store = new AgentExecutionLogStore(dbContext, new FixedTimeProvider(createdAtUtc));
        var added = await store.AddAsync(new AgentExecutionLogInput(agentId, ConversationId: null, MessageId: null, "llama", "h", LatencyMs: 1L, Success: true));
        return added.Id;
    }

    private static long NowMs()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private async Task<ServiceProvider> BuildProviderAsync(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, fileName);
        var services = new ServiceCollection();
        services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IAgentExecutionLogStore, AgentExecutionLogStore>();

        var provider = services.BuildServiceProvider(true);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        return provider;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly long _milliseconds;

        public FixedTimeProvider(long milliseconds)
        {
            _milliseconds = milliseconds;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(_milliseconds);
        }
    }
}
