namespace XE_Local_AI_Engine.Tests.Knowledge;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Tests.Testing;

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

        await worker.ReconcileOnceAsync(CancellationToken.None).ConfigureAwait(false);

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

        await worker.ReconcileOnceAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(1, dispatcher.Attempts.Count);
        AssertEx.Equal(first, dispatcher.Attempts[0]);
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

    private sealed class RecordingDispatcher(KnowledgeIngestionEnqueueResult result) : IKnowledgeIngestionDispatcher
    {
        public List<Guid> Attempts { get; } = [];

        public ValueTask<KnowledgeIngestionEnqueueResult> EnqueueAsync(Guid documentId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts.Add(documentId);
            return ValueTask.FromResult(result);
        }
    }
}
