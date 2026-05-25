namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class NodeChatPersistenceWriterTests
{
    [Test]
    public async Task ExecuteAsync_WhenSameMessageKey_SerializesPersistenceSections()
    {
        await using var provider = BuildServiceProvider();
        var writer = CreateWriter(provider);
        var key = NodeChatPersistenceWriteKey.ForMessage(Guid.NewGuid(), Guid.NewGuid());
        var firstEntered = NewCompletionSource();
        var releaseFirst = NewCompletionSource();
        var secondEntered = NewCompletionSource();
        var activeSections = 0;
        var maxActiveSections = 0;

        var first = writer.ExecuteAsync(key, async (_, token) =>
        {
            TrackEntered(ref activeSections, ref maxActiveSections);
            firstEntered.SetResult();
            await releaseFirst.Task.WaitAsync(token).ConfigureAwait(false);
            Interlocked.Decrement(ref activeSections);
        });

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        var second = writer.ExecuteAsync(key, (_, _) =>
        {
            TrackEntered(ref activeSections, ref maxActiveSections);
            secondEntered.SetResult();
            Interlocked.Decrement(ref activeSections);
            return Task.CompletedTask;
        });

        var secondStartedEarly = await Task.WhenAny(secondEntered.Task, Task.Delay(TimeSpan.FromMilliseconds(100))).ConfigureAwait(false) == secondEntered.Task;
        AssertEx.False(secondStartedEarly, "A second write for the same conversation/message key must wait for the first write section.");

        releaseFirst.SetResult();
        await Task.WhenAll(first, second).ConfigureAwait(false);

        AssertEx.Equal(1, maxActiveSections, "Same-key persistence sections should not overlap.");
        AssertEx.True(secondEntered.Task.IsCompleted, "The queued same-key write should eventually run.");
    }

    [Test]
    public async Task ExecuteAsync_WhenDifferentMessageKeys_AllowsIndependentPersistenceSections()
    {
        await using var provider = BuildServiceProvider();
        var writer = CreateWriter(provider);
        var conversationId = Guid.NewGuid();
        var firstEntered = NewCompletionSource();
        var releaseFirst = NewCompletionSource();
        var secondEntered = NewCompletionSource();

        var first = writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForMessage(conversationId, Guid.NewGuid()), async (_, token) =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task.WaitAsync(token).ConfigureAwait(false);
        });

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        var second = writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForMessage(conversationId, Guid.NewGuid()), (_, _) =>
        {
            secondEntered.SetResult();
            return Task.CompletedTask;
        });

        var secondStarted = await Task.WhenAny(secondEntered.Task, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false) == secondEntered.Task;
        AssertEx.True(secondStarted, "Different message keys should not be blocked by a process-wide active-send gate.");

        releaseFirst.SetResult();
        await Task.WhenAll(first, second).ConfigureAwait(false);
    }

    [Test]
    public async Task ExecuteAsync_CreatesFreshDbContextForEachPersistenceOperation()
    {
        await using var provider = BuildServiceProvider();
        var writer = CreateWriter(provider);
        var key = NodeChatPersistenceWriteKey.ForMessage(Guid.NewGuid(), Guid.NewGuid());

        var firstContextId = await writer.ExecuteAsync(key, (dbContext, _) => Task.FromResult(dbContext.ContextId.InstanceId)).ConfigureAwait(false);
        var secondContextId = await writer.ExecuteAsync(key, (dbContext, _) => Task.FromResult(dbContext.ContextId.InstanceId)).ConfigureAwait(false);

        AssertEx.NotEqual(firstContextId, secondContextId, "Each persistence operation should resolve a fresh NodeChatDbContext from a fresh scope.");
    }

    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite("Data Source=:memory:"));

        return services.BuildServiceProvider(true);
    }

    private static NodeChatPersistenceWriter CreateWriter(ServiceProvider provider)
    {
        return new NodeChatPersistenceWriter(provider.GetRequiredService<IServiceScopeFactory>());
    }

    private static TaskCompletionSource NewCompletionSource()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static void TrackEntered(ref int activeSections, ref int maxActiveSections)
    {
        var active = Interlocked.Increment(ref activeSections);
        int observed;
        do
        {
            observed = Volatile.Read(ref maxActiveSections);
            if (active <= observed)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref maxActiveSections, active, observed) != observed);
    }
}
