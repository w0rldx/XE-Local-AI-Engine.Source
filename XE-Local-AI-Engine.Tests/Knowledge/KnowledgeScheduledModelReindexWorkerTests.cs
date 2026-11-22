namespace XE_Local_AI_Engine.Tests.Knowledge;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class KnowledgeScheduledModelReindexWorkerTests
{
    [Test]
    public async Task ReconcileOnceAsync_EnqueuesEveryStaleDocument()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        catalog.ResetStaleDocumentsToPendingAsync(Arg.Any<CancellationToken>())
               .Returns((IReadOnlyList<Guid>)[first, second]);
        var dispatcher = new RecordingDispatcher(KnowledgeIngestionEnqueueResult.Accepted);
        await using var provider = BuildProvider(catalog);
        using var worker = CreateWorker(provider, dispatcher);

        await worker.ReconcileOnceAsync(CancellationToken.None);

        AssertEx.Equal(2, dispatcher.Attempts.Count);
        AssertEx.Equal(first, dispatcher.Attempts[0]);
        AssertEx.Equal(second, dispatcher.Attempts[1]);
    }

    [Test]
    public async Task ReconcileOnceAsync_WhenQueueIsFull_StopsAdmittingThisTick()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        catalog.ResetStaleDocumentsToPendingAsync(Arg.Any<CancellationToken>())
               .Returns((IReadOnlyList<Guid>)[first, second]);
        var dispatcher = new RecordingDispatcher(KnowledgeIngestionEnqueueResult.QueueFull);
        await using var provider = BuildProvider(catalog);
        using var worker = CreateWorker(provider, dispatcher);

        await worker.ReconcileOnceAsync(CancellationToken.None);

        AssertEx.Equal(1, dispatcher.Attempts.Count);
        AssertEx.Equal(first, dispatcher.Attempts[0]);
    }

    [Test]
    public async Task ReconcileOnceAsync_WhenASweepIndexesTheResetRowBeforePublication_PublishesNoPendingAfterIndexed()
    {
        var documentId = Guid.NewGuid();
        var pushes = new List<string>();
        var notifier = Substitute.For<IKnowledgeIndexingNotifier>();
        _ = notifier.NotifyDocumentChangedAsync(Arg.Any<Guid>(), Arg.Any<KnowledgeDocumentStatus>(), Arg.Any<CancellationToken>())
                    .Returns(call =>
                    {
                        pushes.Add(call.ArgAt<KnowledgeDocumentStatus>(1).ToString());
                        return Task.CompletedTask;
                    });
        _ = notifier.NotifyDocumentsChangedAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            pushes.Add("refresh");
            return Task.CompletedTask;
        });
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        // The reset row is picked up by a drain sweep and indexed before this tick admits it.
        catalog.ResetStaleDocumentsToPendingAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            _ = notifier.NotifyDocumentChangedAsync(documentId, KnowledgeDocumentStatus.Indexed, call.Arg<CancellationToken>());
            return Task.FromResult<IReadOnlyList<Guid>>([documentId]);
        });
        await using var provider = BuildProvider(catalog);
        using var worker = CreateWorker(provider, new KnowledgeIngestionDispatcher(notifier));

        await worker.ReconcileOnceAsync(CancellationToken.None);

        // The follow-up run skips the Indexed row without a push, so a Pending here would hold the feed's batch running.
        AssertEx.Equal("Indexed | refresh", string.Join(" | ", pushes));
    }

    private static ServiceProvider BuildProvider(IKnowledgeDocumentCatalogService catalog)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => catalog);
        return services.BuildServiceProvider();
    }

    private static KnowledgeScheduledModelReindexWorker CreateWorker(ServiceProvider provider,
        IKnowledgeIngestionDispatcher dispatcher)
    {
        return new KnowledgeScheduledModelReindexWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            dispatcher,
            Options.Create(new KnowledgeBaseOptions
            {
                ScheduledModelReindexEnabled = true
            }),
            NullLogger<KnowledgeScheduledModelReindexWorker>.Instance);
    }

    private sealed class RecordingDispatcher : IKnowledgeIngestionDispatcher
    {
        private readonly KnowledgeIngestionEnqueueResult _result;

        public RecordingDispatcher(KnowledgeIngestionEnqueueResult result)
        {
            _result = result;
        }

        public List<Guid> Attempts { get; } = [];

        public ValueTask<KnowledgeIngestionEnqueueResult> EnqueueAsync(Guid documentId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts.Add(documentId);
            return ValueTask.FromResult(_result);
        }
    }
}
