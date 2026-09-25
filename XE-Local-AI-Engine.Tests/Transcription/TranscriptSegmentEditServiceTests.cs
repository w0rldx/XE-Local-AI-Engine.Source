namespace XE_Local_AI_Engine.Tests.Transcription;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Client.Services.Transcription.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The service rules around a transcript-row edit, against the real store: when an edit is refused because the
///     session is still producing rows, and that an allowed edit reads back through the detail view.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class TranscriptSegmentEditServiceTests
{
    [Test]
    public async Task UpdateSegmentText_OnAFinishedSession_PersistsAndReturnsTheRow()
    {
        await using var harness = await TranscriptionServiceHarness.CreateAsync();
        var sessionId = await SeedAsync(harness, TranscriptionSessionStatus.Completed);

        var result = await harness.Service.UpdateSegmentTextAsync(sessionId, seq: 2, "corrected", CancellationToken.None);

        AssertEx.Equal(UpdateTranscriptSegmentOutcome.Updated, result.Outcome);
        var segment = AssertEx.NotNull(result.Segment, "An applied edit answers the row as it now reads.");
        AssertEx.Equal(expected: 2L, segment.Seq);
        AssertEx.Equal("corrected", segment.Text);
        var view = AssertEx.NotNull(await harness.Service.GetSessionAsync(sessionId, CancellationToken.None));
        AssertEx.Equal("first", view.Segments[0].Text, "Only the addressed row changes.");
        AssertEx.Equal("corrected", view.Segments[1].Text);
    }

    [Test]
    public async Task UpdateSegmentText_WhileTheRowIsTranscribing_IsRefusedAndWritesNothing()
    {
        await using var harness = await TranscriptionServiceHarness.CreateAsync();
        var sessionId = await SeedAsync(harness, TranscriptionSessionStatus.Transcribing);

        var result = await harness.Service.UpdateSegmentTextAsync(sessionId, seq: 1, "corrected", CancellationToken.None);

        AssertEx.Equal(UpdateTranscriptSegmentOutcome.SessionTranscribing, result.Outcome);
        AssertEx.Null(result.Segment);
        var view = AssertEx.NotNull(await harness.Service.GetSessionAsync(sessionId, CancellationToken.None));
        AssertEx.Equal("first", view.Segments[0].Text);
    }

    [Test]
    public async Task UpdateSegmentText_WhileTheRegistryStillOwnsTheSession_IsRefused()
    {
        // The drain after Stop: the registry no longer admits audio (not live) but still commits rows (registered). The
        // row can already read as finished, so the registry is the only thing that knows an edit would race a commit.
        var registry = Substitute.For<ILiveTranscriptionSessionRegistry>();
        _ = registry.IsLive(Arg.Any<Guid>()).Returns(false);
        _ = registry.IsRegistered(Arg.Any<Guid>()).Returns(true);
        await using var harness = await TranscriptionServiceHarness.CreateAsync(registry);
        var sessionId = await SeedAsync(harness, TranscriptionSessionStatus.Completed);

        var result = await harness.Service.UpdateSegmentTextAsync(sessionId, seq: 1, "corrected", CancellationToken.None);

        AssertEx.Equal(UpdateTranscriptSegmentOutcome.SessionTranscribing, result.Outcome);
        var view = AssertEx.NotNull(await harness.Service.GetSessionAsync(sessionId, CancellationToken.None));
        AssertEx.Equal("first", view.Segments[0].Text);
    }

    [Test]
    public async Task UpdateSegmentText_ForAnUnknownSessionOrSeq_ReportsWhichIsMissing()
    {
        await using var harness = await TranscriptionServiceHarness.CreateAsync();
        var sessionId = await SeedAsync(harness, TranscriptionSessionStatus.Completed);

        var unknownSession = await harness.Service.UpdateSegmentTextAsync(Guid.NewGuid(), seq: 1, "corrected", CancellationToken.None);
        var unknownSeq = await harness.Service.UpdateSegmentTextAsync(sessionId, seq: 9, "corrected", CancellationToken.None);

        AssertEx.Equal(UpdateTranscriptSegmentOutcome.SessionNotFound, unknownSession.Outcome);
        AssertEx.Equal(UpdateTranscriptSegmentOutcome.SegmentNotFound, unknownSeq.Outcome);
    }

    [Test]
    public async Task UpdateSegmentText_WhenADeleteTakesTheRowBeforeTheReadBack_ReportsSegmentNotFound()
    {
        var sessionId = Guid.NewGuid();
        var store = Substitute.For<ITranscriptionSessionStore>();
        _ = store.GetSummaryAsync(sessionId, Arg.Any<CancellationToken>()).Returns(new TranscriptionSessionSummaryView
        {
            Id = sessionId,
            CreatedAtUtc = 0,
            UpdatedAtUtc = 0,
            Status = TranscriptionSessionStatus.Completed,
            SourceKind = TranscriptionSourceKind.Microphone,
            ModelId = "ggml-tiny",
            ConfigJson = "{}",
            SegmentCount = 1
        });
        _ = store.UpdateSegmentTextAsync(sessionId, 1, "corrected", Arg.Any<long>(), Arg.Any<CancellationToken>())
                 .Returns(TranscriptSegmentUpdateOutcome.Updated);
        _ = store.ListSegmentsAfterAsync(sessionId, 0, 1, Arg.Any<CancellationToken>()).Returns([]);
        await using var provider = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        var service = new TranscriptionService(provider.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<ILiveTranscriptionSessionRegistry>(),
            Substitute.For<ITranscriptionRuntimeService>(),
            Substitute.For<IWhisperServerSupervisor>(),
            Substitute.For<IWhisperTranscriber>(),
            Substitute.For<IAudioTranscoder>(),
            Substitute.For<INodeDataDirectory>(),
            TimeProvider.System,
            NullLogger<TranscriptionService>.Instance);

        var result = await service.UpdateSegmentTextAsync(sessionId, seq: 1, "corrected", CancellationToken.None);

        // Before the fix the vanished read-back threw, which the endpoint answered with a 500.
        AssertEx.Equal(UpdateTranscriptSegmentOutcome.SegmentNotFound, result.Outcome);
        AssertEx.Null(result.Segment);
    }

    // A session holding rows "first" (seq 1) and "second" (seq 2), moved to the given status.
    private static async Task<Guid> SeedAsync(TranscriptionServiceHarness harness, TranscriptionSessionStatus status)
    {
        var session = await harness.Service.CreateSessionAsync(new CreateTranscriptionSessionInput(), CancellationToken.None);
        await harness.Service.AppendLiveSegmentAsync(session.Id, seq: 1, TranscriptChannel.Mono, startMs: 0, endMs: 1_000, "first", confidence: null, CancellationToken.None);
        await harness.Service.AppendLiveSegmentAsync(session.Id, seq: 2, TranscriptChannel.Mono, startMs: 1_000, endMs: 2_000, "second", confidence: null, CancellationToken.None);
        await harness.Service.CompleteLiveAsync(session.Id, status, durationMs: 2_000, detectedLanguage: null, errorCode: null, errorMessage: null, CancellationToken.None);
        return session.Id;
    }
}
