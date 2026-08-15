namespace XE_Local_AI_Engine.Tests.Hubs;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit tests for the hub-backed knowledge indexing notifier. The end-to-end push is covered by
///     <see cref="ServerPushHubTests" />; what only a unit test can prove is the failure contract — a transport hiccup
///     must never propagate into the background ingestion pipeline, and the warning it leaves behind must name the
///     exception type only, never document content.
/// </summary>
public sealed class KnowledgeIndexingNotifierTests
{
    [Test]
    public async Task NotifyDocumentChangedAsync_BroadcastsASanitizedPayloadStampedFromTheClock()
    {
        var documentId = Guid.NewGuid();
        var time = new ManualTimeProvider();
        var (context, clients) = CreateHubContext();
        var notifier = new KnowledgeIndexingNotifier(context, time, new RecordingLogger<KnowledgeIndexingNotifier>());

        await notifier.NotifyDocumentChangedAsync(documentId, KnowledgeDocumentStatus.Chunking);

        await clients.Received(1).SendCoreAsync(KnowledgeBaseHubEvents.DocumentChanged,
            Arg.Is<object?[]>(arguments => Matches(arguments, documentId, time.GetUtcNow().ToUnixTimeMilliseconds())),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NotifyDocumentChangedAsync_WhenTheTransportFails_SwallowsAndLogsTheErrorClassOnly()
    {
        var documentId = Guid.NewGuid();
        var (context, clients) = CreateHubContext();
        _ = clients.SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
                   .ThrowsAsync(new IOException("the connection went away mid-broadcast"));
        var logger = new RecordingLogger<KnowledgeIndexingNotifier>();
        var notifier = new KnowledgeIndexingNotifier(context, new ManualTimeProvider(), logger);

        // Must not throw: ingestion is the caller, and a push failure is not an ingestion failure.
        await notifier.NotifyDocumentChangedAsync(documentId, KnowledgeDocumentStatus.Embedding);

        AssertEx.ContainsSingle(logger.Entries, entry => entry.Level == LogLevel.Warning);
        var message = logger.Entries[0].Message;
        AssertEx.Contains(message, nameof(IOException));
        AssertEx.False(message.Contains("the connection went away mid-broadcast", StringComparison.Ordinal),
            "The warning must carry the exception type, not its message, so nothing content-bearing can leak into the log.");
    }

    private static bool Matches(object?[] arguments, Guid documentId, long occurredAtUtc) =>
        arguments.Length == 1
        && arguments[0] is KnowledgeDocumentChangedHubEvent payload
        && payload.EventType == KnowledgeBaseHubEvents.DocumentChanged
        && payload.DocumentId == documentId
        && payload.OccurredAtUtc == occurredAtUtc;

    private static (IHubContext<KnowledgeBaseHub> Context, IClientProxy All) CreateHubContext()
    {
        var all = Substitute.For<IClientProxy>();
        var clients = Substitute.For<IHubClients>();
        _ = clients.All.Returns(all);
        var context = Substitute.For<IHubContext<KnowledgeBaseHub>>();
        _ = context.Clients.Returns(clients);
        return (context, all);
    }
}
