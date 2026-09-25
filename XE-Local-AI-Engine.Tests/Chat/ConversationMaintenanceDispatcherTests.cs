namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Tests.CodexOAuth;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The post-turn maintenance queue: it never blocks the chat pump, coalesces a second job for a conversation that
///     already has one queued or running, and drops the newest job at capacity.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ConversationMaintenanceDispatcherTests
{
    [Test]
    public void Dispatch_EnqueuesTheJobForTheWorker()
    {
        var dispatcher = Create();
        var job = Job(Guid.NewGuid());

        dispatcher.Dispatch(job);

        AssertEx.True(dispatcher.Reader.TryRead(out var queued));
        AssertEx.True(ReferenceEquals(job, queued), "The worker reads the very job that was dispatched.");
    }

    [Test]
    public void Dispatch_WhenTheConversationAlreadyHasAQueuedJob_CoalescesTheNewOne()
    {
        var dispatcher = Create();
        var conversationId = Guid.NewGuid();

        dispatcher.Dispatch(Job(conversationId));
        dispatcher.Dispatch(Job(conversationId));
        dispatcher.Dispatch(Job(Guid.NewGuid()));

        AssertEx.Equal(expected: 2, dispatcher.Reader.Count, "A second trigger for the same conversation folds into the queued job; another conversation queues its own.");
    }

    [Test]
    public void Dispatch_ADistillAndACompactForOneConversation_QueueSeparatelyInOrder()
    {
        var dispatcher = Create();
        var conversationId = Guid.NewGuid();

        dispatcher.Dispatch(Job(conversationId, ConversationMaintenanceKind.Distill));
        dispatcher.Dispatch(Job(conversationId));
        dispatcher.Dispatch(Job(conversationId, ConversationMaintenanceKind.Distill));

        AssertEx.True(dispatcher.Reader.TryRead(out var first));
        AssertEx.True(dispatcher.Reader.TryRead(out var second));
        AssertEx.Equal(ConversationMaintenanceKind.Distill, first!.Kind);
        AssertEx.Equal(ConversationMaintenanceKind.Compact, second!.Kind);
        AssertEx.Equal(expected: 0, dispatcher.Reader.Count, "Coalescing is per (conversation, kind): the second distill folds into the first.");
    }

    [Test]
    public void Dispatch_WhileTheJobIsRunning_CoalescesUntilTheWorkerCompletesIt()
    {
        var dispatcher = Create();
        var conversationId = Guid.NewGuid();
        dispatcher.Dispatch(Job(conversationId));
        AssertEx.True(dispatcher.Reader.TryRead(out var running));

        // Dequeued but not completed is "running": the slot is still held.
        dispatcher.Dispatch(Job(conversationId));
        AssertEx.Equal(expected: 0, dispatcher.Reader.Count);

        dispatcher.Complete(running!);
        dispatcher.Dispatch(Job(conversationId));
        AssertEx.Equal(expected: 1, dispatcher.Reader.Count, "Once the worker completes the job, the next trigger queues again.");
    }

    [Test]
    public void Dispatch_WhenTheQueueIsFull_DropsTheNewestWithAWarningAndReleasesItsSlot()
    {
        var logger = new CapturingLogger<ConversationMaintenanceDispatcher>();
        var dispatcher = new ConversationMaintenanceDispatcher(Options.Create(new ConversationCompactionOptions
        {
            MaintenanceQueueCapacity = 2
        }), logger);
        var dropped = Guid.NewGuid();

        dispatcher.Dispatch(Job(Guid.NewGuid()));
        dispatcher.Dispatch(Job(Guid.NewGuid()));
        dispatcher.Dispatch(Job(dropped));

        AssertEx.Equal(expected: 2, dispatcher.Reader.Count, "The bounded queue holds its capacity and no more; Dispatch returned without blocking.");
        AssertEx.Contains(logger.AllText, "queue is full", message: "A dropped job is reported, content-free.");

        // The dropped job must not leave its coalescing slot behind, or that conversation would never compact again.
        AssertEx.True(dispatcher.Reader.TryRead(out var first));
        dispatcher.Complete(first!);
        dispatcher.Dispatch(Job(dropped));
        AssertEx.Equal(expected: 2, dispatcher.Reader.Count);
    }

    [Test]
    public void Dispatch_AfterTheWriterCompleted_DropsWithoutThrowing()
    {
        var dispatcher = Create();
        dispatcher.CompleteWriter();

        dispatcher.Dispatch(Job(Guid.NewGuid()));

        AssertEx.Equal(expected: 0, dispatcher.Reader.Count);
    }

    private static ConversationMaintenanceDispatcher Create() =>
        new(Options.Create(new ConversationCompactionOptions()), NullLogger<ConversationMaintenanceDispatcher>.Instance);

    private static ConversationMaintenanceJob Job(Guid conversationId, ConversationMaintenanceKind kind = ConversationMaintenanceKind.Compact) =>
        new()
        {
            ConversationId = conversationId,
            Kind = kind,
            ModelName = "local-model",
            ContextCapacityTokens = 8_192,
            ReservedOutputTokens = 1_024
        };
}
