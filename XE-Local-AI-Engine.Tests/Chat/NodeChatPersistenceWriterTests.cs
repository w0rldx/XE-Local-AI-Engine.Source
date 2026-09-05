namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class NodeChatPersistenceWriterTests
{
    [Test]
    public async Task ExecuteConversationExclusiveAsync_SerializesTwoWritersOnTheSameConversation()
    {
        await using var provider = BuildServiceProvider();
        var writer = CreateWriter(provider);
        var conversationId = Guid.NewGuid();
        var firstEntered = NewCompletionSource();
        var releaseFirst = NewCompletionSource();
        var secondEntered = NewCompletionSource();
        var activeSections = 0;
        var maxActiveSections = 0;

        var first = writer.ExecuteConversationExclusiveAsync(conversationId, async (_, token) =>
        {
            TrackEntered(ref activeSections, ref maxActiveSections);
            firstEntered.SetResult();
            await releaseFirst.Task.WaitAsync(token).ConfigureAwait(false);
            Interlocked.Decrement(ref activeSections);
            return true;
        });

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        var second = writer.ExecuteConversationExclusiveAsync(conversationId, (_, _) =>
        {
            TrackEntered(ref activeSections, ref maxActiveSections);
            secondEntered.SetResult();
            Interlocked.Decrement(ref activeSections);
            return Task.FromResult(true);
        });

        await AssertEx.StaysIncompleteAsync(secondEntered.Task, "A second exclusive write on the same conversation must wait for the first.")
                      .ConfigureAwait(false);

        releaseFirst.SetResult();
        await Task.WhenAll(first, second).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, maxActiveSections, "Two exclusive writes on one conversation must never overlap.");
    }

    [Test]
    public async Task ExecuteConversationExclusiveAsync_DoesNotBlockAcrossDifferentConversations()
    {
        await using var provider = BuildServiceProvider();
        var writer = CreateWriter(provider);
        var firstEntered = NewCompletionSource();
        var releaseFirst = NewCompletionSource();
        var secondEntered = NewCompletionSource();

        var first = writer.ExecuteConversationExclusiveAsync(Guid.NewGuid(), async (_, token) =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task.WaitAsync(token).ConfigureAwait(false);
            return true;
        });

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        var second = writer.ExecuteConversationExclusiveAsync(Guid.NewGuid(), (_, _) =>
        {
            secondEntered.SetResult();
            return Task.FromResult(true);
        });

        await AssertEx.CompletesAsync(secondEntered.Task, TestBudgets.Contended, "Exclusive writes on different conversations must run independently.")
                      .ConfigureAwait(false);

        releaseFirst.SetResult();
        await Task.WhenAll(first, second).ConfigureAwait(false);
    }

    [Test]
    public async Task ExecuteMessageUpdateAsync_RunsInParallelAcrossDifferentMessages()
    {
        await using var provider = BuildServiceProvider();
        var writer = CreateWriter(provider);
        var conversationId = Guid.NewGuid();
        var firstEntered = NewCompletionSource();
        var releaseFirst = NewCompletionSource();
        var secondEntered = NewCompletionSource();

        var first = writer.ExecuteMessageUpdateAsync(conversationId, Guid.NewGuid(), async (_, token) =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task.WaitAsync(token).ConfigureAwait(false);
            return true;
        });

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        var second = writer.ExecuteMessageUpdateAsync(conversationId, Guid.NewGuid(), (_, _) =>
        {
            secondEntered.SetResult();
            return Task.FromResult(true);
        });

        await AssertEx.CompletesAsync(secondEntered.Task, TestBudgets.Contended, "Payload updates to different messages must run in parallel (shared conversation lock).")
                      .ConfigureAwait(false);

        releaseFirst.SetResult();
        await Task.WhenAll(first, second).ConfigureAwait(false);
    }

    [Test]
    public async Task ExecuteMessageUpdateAsync_SerializesTwoUpdatesToTheSameMessage()
    {
        await using var provider = BuildServiceProvider();
        var writer = CreateWriter(provider);
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var firstEntered = NewCompletionSource();
        var releaseFirst = NewCompletionSource();
        var secondEntered = NewCompletionSource();
        var activeSections = 0;
        var maxActiveSections = 0;

        var first = writer.ExecuteMessageUpdateAsync(conversationId, messageId, async (_, token) =>
        {
            TrackEntered(ref activeSections, ref maxActiveSections);
            firstEntered.SetResult();
            await releaseFirst.Task.WaitAsync(token).ConfigureAwait(false);
            Interlocked.Decrement(ref activeSections);
            return true;
        });

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        var second = writer.ExecuteMessageUpdateAsync(conversationId, messageId, (_, _) =>
        {
            TrackEntered(ref activeSections, ref maxActiveSections);
            secondEntered.SetResult();
            Interlocked.Decrement(ref activeSections);
            return Task.FromResult(true);
        });

        await AssertEx.StaysIncompleteAsync(secondEntered.Task, "Two updates to the same message must serialize on the per-message lock.")
                      .ConfigureAwait(false);

        releaseFirst.SetResult();
        await Task.WhenAll(first, second).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, maxActiveSections, "Same-message updates must not overlap.");
    }

    [Test]
    public async Task ExecuteConversationExclusiveAsync_ExcludesAnInFlightMessageUpdate()
    {
        await using var provider = BuildServiceProvider();
        var writer = CreateWriter(provider);
        var conversationId = Guid.NewGuid();
        var updateEntered = NewCompletionSource();
        var releaseUpdate = NewCompletionSource();
        var exclusiveEntered = NewCompletionSource();

        var update = writer.ExecuteMessageUpdateAsync(conversationId, Guid.NewGuid(), async (_, token) =>
        {
            updateEntered.SetResult();
            await releaseUpdate.Task.WaitAsync(token).ConfigureAwait(false);
            return true;
        });

        await updateEntered.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        // A conversation-exclusive op (e.g. delete) must wait for the in-flight shared message update to finish.
        var exclusive = writer.ExecuteConversationExclusiveAsync(conversationId, (_, _) =>
        {
            exclusiveEntered.SetResult();
            return Task.FromResult(true);
        });

        await AssertEx.StaysIncompleteAsync(exclusiveEntered.Task, "A conversation-exclusive op must not run while a message update holds the shared lock.")
                      .ConfigureAwait(false);

        releaseUpdate.SetResult();
        await Task.WhenAll(update, exclusive).ConfigureAwait(false);
        AssertEx.True(exclusiveEntered.Task.IsCompleted, "The exclusive op must run once the message update releases the shared lock.");
    }

    [Test]
    public async Task Execute_CreatesFreshDbContextForEachOperation()
    {
        await using var provider = BuildServiceProvider();
        var writer = CreateWriter(provider);
        var conversationId = Guid.NewGuid();

        var firstContextId = await writer.ExecuteConversationExclusiveAsync(conversationId, (dbContext, _) => Task.FromResult(dbContext.ContextId.InstanceId)).ConfigureAwait(false);
        var secondContextId = await writer.ExecuteConversationExclusiveAsync(conversationId, (dbContext, _) => Task.FromResult(dbContext.ContextId.InstanceId)).ConfigureAwait(false);

        AssertEx.NotEqual(firstContextId, secondContextId, "Each operation should resolve a fresh NodeChatDbContext from a fresh scope.");
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
