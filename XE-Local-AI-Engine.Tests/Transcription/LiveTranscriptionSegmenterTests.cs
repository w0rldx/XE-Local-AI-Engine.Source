namespace XE_Local_AI_Engine.Tests.Transcription;

using System.Buffers.Binary;
using System.Globalization;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Client.Services.Transcription.Live;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The live lane's state machine, driven by synthetic audio whose every millisecond is self-identifying.
/// </summary>
/// <remarks>
///     The PCM these tests push is a pattern: the first four bytes of each thirty-two-byte millisecond carry that
///     millisecond's index. The fake transcriber decodes the submitted payload back into an absolute range and
///     verifies the blocks are contiguous, so an assertion about a window's bounds is an assertion about the bytes
///     that were actually sent — which is the only way a test can tell "the watermark moved" from "the audio was
///     really dropped from the buffer".
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class LiveTranscriptionSegmenterTests
{
    private const string ModelId = "ggml-base";

    [Test]
    public async Task SilenceOnlyPushes_CommitNothingAndLeaveTheWatermark()
    {
        var transcriber = new ScriptedWhisperTranscriber(_ => []);
        var segmenter = Create(transcriber, Settings(maxWindowSeconds: 5));

        var ticks = await PushAsync(segmenter, 0, 3_000, 500);

        AssertEx.NotEmpty(transcriber.Windows, "The ticks still submit; it is the model that reports nothing.");
        AssertEx.Empty(ticks.SelectMany(tick => tick.Commits), "Audio the model found no speech in never becomes a segment.");
        AssertEx.Equal(0L, segmenter.CommittedEndMs, "A silent stretch must not move the watermark under the normal rule.");
        AssertEx.Equal(string.Empty, ticks[^1].Partial, "Silence has no provisional text either.");
    }

    [Test]
    public async Task WhenTheTailExceedsTheMaxWindow_ForceCommitsToTheLastReturnedSegmentEnd()
    {
        var transcriber = new ScriptedWhisperTranscriber(window => window.DurationMs == 2_000
            ?
            [
                new WhisperTranscriptSegment { StartSeconds = 0.0, EndSeconds = 0.9, Text = "alpha", Confidence = 0.8 },
                new WhisperTranscriptSegment { StartSeconds = 0.9, EndSeconds = 1.7, Text = "beta", Confidence = 0.7 }
            ]
            : []);
        var segmenter = Create(transcriber, Settings(maxWindowSeconds: 2));

        var ticks = await PushAsync(segmenter, 0, 2_000, 500);

        var commits = ticks.SelectMany(tick => tick.Commits).ToList();
        AssertEx.Equal(2, commits.Count, "Both returned segments commit: the tail guard is suspended at the cap.");
        AssertEx.Equal(900L, commits[0].EndMs, "The first segment keeps the model's own end time.");
        AssertEx.Equal(1_700L, commits[1].EndMs, "So does the last one.");
        AssertEx.Equal(1_700L, segmenter.CommittedEndMs, "The watermark lands on the last committed end, not on the window end.");
    }

    [Test]
    public async Task WhenTheTailExceedsTheMaxWindow_WithNoReturnedSegments_AdvancesTheWatermarkWithoutEmittingARow()
    {
        var transcriber = new ScriptedWhisperTranscriber(_ => []);
        var segmenter = Create(transcriber, Settings(maxWindowSeconds: 2));

        var ticks = await PushAsync(segmenter, 0, 2_000, 500);

        AssertEx.Empty(ticks.SelectMany(tick => tick.Commits), "A window the model found nothing in emits no row.");
        AssertEx.Equal(2_000L, segmenter.CommittedEndMs, "It is still dropped, or the cap could never clear the buffer.");
    }

    [Test]
    public async Task ASegmentStartingBeforeTheWatermarkIsDropped()
    {
        // The two returned segments overlap, which is exactly what the real model does across window boundaries: the
        // second repeats words the first already carried.
        var transcriber = new ScriptedWhisperTranscriber(window => window.DurationMs == 2_000
            ?
            [
                new WhisperTranscriptSegment { StartSeconds = 0.0, EndSeconds = 1.0, Text = "and so my fellow", Confidence = 0.9 },
                new WhisperTranscriptSegment { StartSeconds = 0.5, EndSeconds = 1.5, Text = "my fellow americans", Confidence = 0.9 }
            ]
            : []);
        var segmenter = Create(transcriber, Settings(maxWindowSeconds: 2));

        var ticks = await PushAsync(segmenter, 0, 2_000, 500);

        var commits = ticks.SelectMany(tick => tick.Commits).ToList();
        AssertEx.Equal(1, commits.Count, "The overlapping repeat is dropped by the watermark, with no text comparison.");
        AssertEx.Equal("and so my fellow", commits[0].Text, "The first segment is the one that survives.");
        AssertEx.Equal(1_000L, segmenter.CommittedEndMs, "The watermark is the end of what was committed.");
    }

    [Test]
    public async Task PushesBelowTheTickBoundary_MakeNoTranscriberCall()
    {
        var transcriber = new ScriptedWhisperTranscriber(ContinuousSpeech);
        var segmenter = Create(transcriber, Settings(maxWindowSeconds: 5));

        _ = await PushAsync(segmenter, 0, 999, 37);

        AssertEx.Equal(0, transcriber.CallCount, "A tick runs when audio time crosses the boundary, not when a frame arrives.");
        AssertEx.Equal(999L, segmenter.AudioEndMs, "The clock still advanced with every frame.");
    }

    [Test]
    public async Task Flush_CommitsEverythingIncludingTheGuardedTail()
    {
        var transcriber = new ScriptedWhisperTranscriber(ContinuousSpeech);
        var segmenter = Create(transcriber, Settings(maxWindowSeconds: 5));

        var ticks = await PushAsync(segmenter, 0, 1_500, 500);
        AssertEx.Empty(ticks.SelectMany(tick => tick.Commits), "A segment touching the end of the window is held back by the guard.");
        AssertEx.NotEmpty(ticks[^1].Partial, "It is provisional text in the meantime.");

        var flush = await segmenter.FlushAsync(CancellationToken.None);

        AssertEx.Equal(1, flush.Commits.Count, "The flush suspends the guard, so the held tail commits.");
        AssertEx.Equal(0L, flush.Commits[0].StartMs, "It covers the whole retained span.");
        AssertEx.Equal(1_500L, flush.Commits[0].EndMs, "Up to the last millisecond received.");
        AssertEx.Equal(1_500L, segmenter.CommittedEndMs, "And the watermark finishes at the end of the audio.");
        AssertEx.Equal(string.Empty, flush.Partial, "Nothing is left provisional after a successful flush.");
    }

    [Test]
    [Arguments(2)]
    [Arguments(5)]
    [Arguments(10)]
    public async Task EveryMillisecondIsSubmittedBeforeItIsCommittedPast(int maxWindowSeconds)
    {
        // Twice per cap: continuous speech, where every response ends at the window end and the guard blocks every
        // ordinary commit, and empty responses, where the cap is the only thing that ever moves the watermark. The
        // first is the shape that cropping-before-committing destroys audio in.
        foreach (var (mode, respond) in new (string Mode, Func<SubmittedWindow, IReadOnlyList<WhisperTranscriptSegment>> Respond)[]
                 {
                     ("continuous speech", ContinuousSpeech),
                     ("empty responses", _ => [])
                 })
        {
            var transcriber = new ScriptedWhisperTranscriber(respond);
            var segmenter = Create(transcriber, Settings(maxWindowSeconds));

            _ = await PushAsync(segmenter, 0, 12_000, 500);

            AssertEx.True(segmenter.CommittedEndMs > 0, $"The cap must move the watermark with {mode} at a {maxWindowSeconds} s window.");
            AssertCoversWithoutAHole(transcriber.Windows,
                segmenter.CommittedEndMs,
                $"Every millisecond below the watermark was submitted with {mode} at a {maxWindowSeconds} s window.");
        }
    }

    [Test]
    public async Task AtTheCap_SubmitsTheWholeUncommittedSpanNotACroppedWindow()
    {
        // The counterexample the cap-first rule exists for: with a 5 s cap and every response ending at the window
        // end, a crop-first segmenter submits [1s, 6s] and then force-commits past [0s, 1s], which no request ever
        // carried.
        var transcriber = new ScriptedWhisperTranscriber(ContinuousSpeech);
        var segmenter = Create(transcriber, Settings(maxWindowSeconds: 5));

        _ = await PushAsync(segmenter, 0, 6_000, 500);

        AssertEx.ContainsSingle(transcriber.Windows,
            window => window.StartMs == 0 && window.EndMs == 5_000,
            "The at-cap request must start at 0 ms and carry the whole uncommitted span.");
        AssertEx.Equal(5_000L, segmenter.CommittedEndMs, "And the watermark follows what that request returned.");
    }

    [Test]
    public async Task AtTheCapWithNoSegments_RemovesTheAudioFromTheBufferNotOnlyTheWatermark()
    {
        var transcriber = new ScriptedWhisperTranscriber(_ => []);
        var segmenter = Create(transcriber, Settings(maxWindowSeconds: 2));

        _ = await PushAsync(segmenter, 0, 4_000, 500);

        // The fake decodes the submitted payload's own millisecond indices, so a segmenter that moved the watermark
        // without dropping the bytes would submit the stale prefix here and this range would read [0, 2000).
        AssertEx.ContainsSingle(transcriber.Windows,
            window => window.StartMs == 2_000 && window.EndMs == 4_000,
            "The second cap window carries the audio after the first one, not the buffer from the start of the session.");
        AssertEx.Equal(4_000L, segmenter.CommittedEndMs, "Both cap windows cleared.");
    }

    [Test]
    public async Task A750MillisecondRecording_IsSubmittedOnFlush()
    {
        var transcriber = new ScriptedWhisperTranscriber(ContinuousSpeech);
        var segmenter = Create(transcriber, Settings(maxWindowSeconds: 5));

        _ = await PushAsync(segmenter, 0, 750, 250);
        AssertEx.Equal(0, transcriber.CallCount, "A recording shorter than one tick fires no tick.");

        var flush = await segmenter.FlushAsync(CancellationToken.None);

        AssertEx.Equal(1, transcriber.CallCount, "The flush submits it anyway, or the whole dictation transcribes to nothing.");
        AssertEx.Equal(0L, transcriber.Windows[0].StartMs, "From the start of the recording.");
        AssertEx.Equal(750L, transcriber.Windows[0].EndMs, "To its end.");
        AssertEx.Equal(1, flush.Commits.Count, "And what came back is durable.");
    }

    [Test]
    public async Task ASubSecondSuffixAfterACommit_IsSubmittedOnFlush()
    {
        var transcriber = new ScriptedWhisperTranscriber(window => window.DurationMs == 1_000
            ? [new WhisperTranscriptSegment { StartSeconds = 0.0, EndSeconds = 0.9, Text = "first", Confidence = 0.9 }]
            : [new WhisperTranscriptSegment { StartSeconds = 0.0, EndSeconds = window.DurationMs / 1000.0, Text = "second", Confidence = 0.9 }]);
        var segmenter = Create(transcriber, Settings(maxWindowSeconds: 5, tailGuardMs: 100));

        _ = await PushAsync(segmenter, 0, 1_000, 500);
        AssertEx.Equal(900L, segmenter.CommittedEndMs, "The first tick commits inside the shorter guard.");

        _ = await PushAsync(segmenter, 1_000, 1_300, 300);
        AssertEx.Equal(1, transcriber.CallCount, "The 300 ms suffix fires no tick of its own.");

        var flush = await segmenter.FlushAsync(CancellationToken.None);

        AssertEx.Equal(900L, transcriber.Windows[^1].StartMs, "The flush picks up where the commit left off.");
        AssertEx.Equal(1_300L, transcriber.Windows[^1].EndMs, "And carries the whole sub-second suffix.");
        AssertEx.ContainsSingle(flush.Commits, commit => commit.Text == "second", "The suffix reaches the transcript.");
    }

    [Test]
    public async Task FlushThatThrows_LeavesTheWatermarkAndTheRetainedAudioIntact()
    {
        var transcriber = new ScriptedWhisperTranscriber(ContinuousSpeech);
        var segmenter = Create(transcriber, Settings(maxWindowSeconds: 5));
        _ = await PushAsync(segmenter, 0, 750, 250);

        transcriber.Failure = new InvalidOperationException("the runtime went away");
        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(async () => await segmenter.FlushAsync(CancellationToken.None),
            "A failed finalization surfaces rather than pretending the audio was transcribed.");

        AssertEx.Equal(0L, segmenter.CommittedEndMs, "A failed flush must not move the watermark.");
        AssertEx.Equal(750L, segmenter.AudioEndMs, "The clock is unchanged too.");

        transcriber.Failure = null;
        var retry = await segmenter.FlushAsync(CancellationToken.None);

        AssertEx.Equal(0L, transcriber.Windows[^1].StartMs, "The retained audio was still there for the retry.");
        AssertEx.Equal(750L, transcriber.Windows[^1].EndMs, "All of it.");
        AssertEx.Equal(1, retry.Commits.Count, "So one failed request cost nothing but a round trip.");
    }

    [Test]
    public async Task Timing_IsIdenticalUnderTwoByteAndSixteenKilobyteFramePartitions()
    {
        var coarse = new ScriptedWhisperTranscriber(ContinuousSpeech);
        var coarseSegmenter = Create(coarse, Settings(maxWindowSeconds: 2));
        var fine = new ScriptedWhisperTranscriber(ContinuousSpeech);
        var fineSegmenter = Create(fine, Settings(maxWindowSeconds: 2));

        var coarseTicks = await PushRawAsync(coarseSegmenter, LivePcm.Range(0, 4_000), 16_384);
        var fineTicks = await PushRawAsync(fineSegmenter, LivePcm.Range(0, 4_000), 2);

        AssertEx.Equal(string.Join(';', coarse.Windows), string.Join(';', fine.Windows), "The frame partition must not change which windows are submitted.");
        AssertEx.Equal(Ranges(coarseTicks), Ranges(fineTicks), "Nor which segments commit, nor when.");
        AssertEx.Equal(coarseSegmenter.AudioEndMs, fineSegmenter.AudioEndMs, "Both partitions carry the same audio time.");
        AssertEx.Equal(coarseSegmenter.AudioEndMs - coarseSegmenter.CommittedEndMs,
            fineSegmenter.AudioEndMs - fineSegmenter.CommittedEndMs,
            "And retain the same bounded tail; a per-frame clock would retain everything under two-byte frames.");
    }

    [Test]
    public async Task RepeatedTwoByteFrames_AdvanceTheClockAndEventuallyTick()
    {
        var transcriber = new ScriptedWhisperTranscriber(ContinuousSpeech);
        var segmenter = Create(transcriber, Settings(maxWindowSeconds: 5));

        // Sixteen of these carry one millisecond, so a clock accumulated per frame would divide 2 by 32 sixteen times,
        // stay at zero forever and never fire a tick.
        _ = await PushRawAsync(segmenter, LivePcm.Range(0, 1_000), 2);

        AssertEx.Equal(1_000L, segmenter.AudioEndMs, "The cumulative byte count is what the clock is derived from.");
        AssertEx.Equal(1, transcriber.CallCount, "So the tick at one second fires exactly once.");
    }

    [Test]
    [Arguments(2)]
    [Arguments(5)]
    [Arguments(10)]
    public async Task MaximumRequestDurationNeverExceedsTheCap(int maxWindowSeconds)
    {
        var transcriber = new ScriptedWhisperTranscriber(ContinuousSpeech);
        var segmenter = Create(transcriber, Settings(maxWindowSeconds));

        // One 60 s push, which is the shape an in-process producer can legitimately deliver. A segmenter that
        // submitted "the whole uncommitted span, however long" would make a single 60 s inference request here.
        _ = await segmenter.PushAsync(LivePcm.Range(0, 60_000), CancellationToken.None);

        AssertEx.NotEmpty(transcriber.Windows, "The push is processed, not buffered whole.");
        AssertEx.Equal(maxWindowSeconds * 1_000L,
            transcriber.Windows.Max(window => window.DurationMs),
            $"No inference window may exceed the {maxWindowSeconds} s cap.");
    }

    [Test]
    public async Task MultiWindowPush_SubmitsCapSizedWindowsInOrderCoveringEveryMillisecond()
    {
        var transcriber = new ScriptedWhisperTranscriber(_ => []);
        var segmenter = Create(transcriber, Settings(maxWindowSeconds: 2));

        _ = await segmenter.PushAsync(LivePcm.Range(0, 20_000), CancellationToken.None);

        var starts = transcriber.Windows.Select(window => window.StartMs).ToList();
        AssertEx.Equal(string.Join(',', starts.OrderBy(start => start)),
            string.Join(',', starts),
            "A large push is split into windows submitted in order.");
        AssertEx.Equal(20_000L, segmenter.CommittedEndMs, "The whole push is resolved, not left half-buffered.");
        AssertCoversWithoutAHole(transcriber.Windows, segmenter.CommittedEndMs, "The split windows cover every millisecond of the push.");
    }

    [Test]
    public async Task PushWithAnAlreadyCancelledToken_DoesNotAcceptAudio()
    {
        var transcriber = new ScriptedWhisperTranscriber(ContinuousSpeech);
        var segmenter = Create(transcriber, Settings(maxWindowSeconds: 2));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(
            async () => await segmenter.PushAsync(LivePcm.Range(0, 500), cancellation.Token),
            "Even a frame shorter than the inference tick must observe cancellation before accepting audio.");

        AssertEx.Equal(0L, segmenter.AudioEndMs);
        AssertEx.Equal(0L, segmenter.CommittedEndMs);
        AssertEx.Equal(0, transcriber.CallCount);
    }

    [Test]
    public async Task CancelledSubmission_FollowedByQueuedFrames_DoesNotBecomeAStall()
    {
        var transcriber = new GatedWhisperTranscriber(ContinuousSpeech);
        var segmenter = Create(transcriber, Settings(maxWindowSeconds: 2));
        using var cancellation = new CancellationTokenSource();

        var submission = segmenter.PushAsync(LivePcm.Range(0, 1_000), cancellation.Token).AsTask();
        try
        {
            await transcriber.Entered.WaitAsync(TestBudgets.Contended);
        }
        finally
        {
            await cancellation.CancelAsync();
        }

        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(async () => await submission,
            "Cancellation interrupts the in-flight submission at its current boundary.");

        // The registry drains previously admitted frames serially with the same cancelled lane token.
        for (var frame = 0; frame < 4; frame++)
        {
            _ = await AssertEx.ThrowsAsync<OperationCanceledException>(
                async () => await segmenter.PushAsync(LivePcm.Range(1_000, 1_500), cancellation.Token),
                "Cancelled queued frames must not increment the unchanged-boundary stall counter.");
            AssertEx.Equal(1_000L, segmenter.AudioEndMs, "No queued audio is accepted after cancellation.");
            AssertEx.Equal(0L, segmenter.CommittedEndMs, "The cancelled submission committed no audio.");
        }

        AssertEx.Equal(1, transcriber.CallCount);
        AssertEx.False(transcriber.Released, "The inference gate was cancelled, not released to produce a successful result.");
    }

    [Test]
    public async Task WhenASubmissionMakesNoProgressTwice_ThrowsLiveSegmenterStalled()
    {
        // A transcriber that answers every window with a zero-length segment at the very start of it: each response
        // commits something, so nothing looks broken, and yet neither the watermark nor the buffer ever moves.
        var transcriber = new ScriptedWhisperTranscriber(_ => [new WhisperTranscriptSegment { StartSeconds = 0.0, EndSeconds = 0.0, Text = "x", Confidence = 0.5 }]);
        var segmenter = Create(transcriber, Settings(maxWindowSeconds: 2));

        _ = await AssertEx.ThrowsAsync<LiveSegmenterStalledException>(async () => await segmenter.PushAsync(LivePcm.Range(0, 4_000), CancellationToken.None),
            "A lane that cannot progress fails the session instead of discarding the audio it cannot resolve.");
    }

    [Test]
    public async Task DetectedLanguage_IsAskedForOncePerSessionAndThenRemembered()
    {
        var transcriber = new ScriptedWhisperTranscriber(ContinuousSpeech)
        {
            DetectedLanguageCode = "en"
        };
        var segmenter = Create(transcriber, Settings(maxWindowSeconds: 5));

        var ticks = await PushAsync(segmenter, 0, 3_000, 500);

        AssertEx.Equal("en", segmenter.DetectedLanguageCode, "The first code any window reports is the session's.");
        AssertEx.ContainsSingle(ticks, tick => tick.DetectedLanguage == "en", "It is reported once, on the call that learned it.");
        AssertEx.Equal("True,False,False",
            string.Join(',', transcriber.DetectLanguageFlags),
            "The probability pass is paid once per session, not once per second.");
    }

    [Test]
    [Arguments(null, 5)]
    [Arguments(1, 2)]
    [Arguments(2, 2)]
    [Arguments(7, 7)]
    [Arguments(10, 10)]
    [Arguments(600, 10)]
    public void Settings_ClampTheOperatorsWindowIntoTheAllowedRange(int? requested, int expected) =>
        AssertEx.Equal(expected,
            LiveSegmenterSettings.FromSessionConfig(requested).MaxWindowSeconds,
            $"A stored window of {requested?.ToString(CultureInfo.InvariantCulture) ?? "null"} resolves to {expected} s.");

    [Test]
    public async Task AtTheCap_ASegmentOverrunningTheWindowEndIsCommittedNotDropped()
    {
        // The model reports an end time 20 ms past the audio it was given, which VAD padding does routinely. The cap
        // frees that audio whatever happens next, so a segment rejected here is a segment lost outright.
        var transcriber = new ScriptedWhisperTranscriber(window => window.DurationMs == 2_000
            ? [new WhisperTranscriptSegment { StartSeconds = 0.0, EndSeconds = (window.DurationMs + 20) / 1000.0, Text = "and so my fellow americans", Confidence = 0.9 }]
            : []);
        var segmenter = Create(transcriber, Settings(maxWindowSeconds: 2));

        var ticks = await PushAsync(segmenter, 0, 2_000, 500);

        var commits = ticks.SelectMany(tick => tick.Commits).ToList();
        AssertEx.Equal(1, commits.Count, "An overrunning segment is the model's answer for this window and must still commit.");
        AssertEx.Equal(2_020L, commits[0].EndMs, "The commit keeps the model's own end time, unclamped.");
        AssertEx.Equal(2_000L, segmenter.CommittedEndMs, "Only the watermark is clamped, so the lane never drops audio it did not receive.");
    }

    [Test]
    public async Task Flush_ASegmentOverrunningTheWindowEndIsCommittedNotDropped()
    {
        var transcriber = new ScriptedWhisperTranscriber(window =>
            [new WhisperTranscriptSegment { StartSeconds = 0.0, EndSeconds = (window.DurationMs + 20) / 1000.0, Text = "ask what you can do", Confidence = 0.9 }]);
        var segmenter = Create(transcriber, Settings(maxWindowSeconds: 5));

        _ = await PushAsync(segmenter, 0, 750, 250);
        var flush = await segmenter.FlushAsync(CancellationToken.None);

        AssertEx.Equal(1, flush.Commits.Count, "A flush is the last word on this audio; rejecting the overrun loses the whole recording.");
        AssertEx.Equal(770L, flush.Commits[0].EndMs, "The commit keeps the model's own end time.");
        AssertEx.Equal(750L, segmenter.CommittedEndMs, "The watermark stops at the audio that exists.");
        AssertEx.Equal(string.Empty, flush.Partial, "And nothing is left provisional.");
    }

    [Test]
    public async Task BelowTheCap_ASegmentInsideTheGuardStaysPending()
    {
        // The negative control for the two above: away from the cap and the flush the guard is real, and a segment
        // reaching the end of the window is held back rather than committed early.
        var transcriber = new ScriptedWhisperTranscriber(ContinuousSpeech);
        var segmenter = Create(transcriber, Settings(maxWindowSeconds: 5));

        var ticks = await PushAsync(segmenter, 0, 1_000, 500);

        AssertEx.Empty(ticks.SelectMany(tick => tick.Commits), "An ordinary tick still honours the tail guard.");
        AssertEx.Equal(0L, segmenter.CommittedEndMs, "So the watermark has not moved.");
        AssertEx.NotEmpty(ticks[^1].Partial, "The text is provisional, not lost.");
    }

    private static LiveSegmenterSettings Settings(int maxWindowSeconds, int tailGuardMs = 800, int tickMs = 1_000) =>
        new()
        {
            MaxWindowSeconds = maxWindowSeconds,
            TailGuardMs = tailGuardMs,
            TickMs = tickMs
        };

    private static LiveTranscriptionSegmenter Create(IWhisperTranscriber transcriber, LiveSegmenterSettings settings) =>
        new(transcriber, TranscriptChannel.Mono, ModelId, languageCode: null, translate: false, settings);

    /// <summary>One segment covering the whole submitted window, which is what a speaker talking without pause gives.</summary>
    private static IReadOnlyList<WhisperTranscriptSegment> ContinuousSpeech(SubmittedWindow window) =>
        [new WhisperTranscriptSegment { StartSeconds = 0.0, EndSeconds = window.DurationMs / 1000.0, Text = $"w{window.StartMs}-{window.EndMs}", Confidence = 0.9 }];

    private static async Task<List<LiveTick>> PushAsync(LiveTranscriptionSegmenter segmenter, long fromMs, long toMs, int frameMs)
    {
        var ticks = new List<LiveTick>();
        for (var at = fromMs; at < toMs; at += frameMs)
        {
            var until = Math.Min(at + frameMs, toMs);
            ticks.Add(await segmenter.PushAsync(LivePcm.Range(at, until), CancellationToken.None));
        }

        return ticks;
    }

    private static async Task<List<LiveTick>> PushRawAsync(LiveTranscriptionSegmenter segmenter, ReadOnlyMemory<byte> pcm, int frameBytes)
    {
        var ticks = new List<LiveTick>();
        for (var offset = 0; offset < pcm.Length; offset += frameBytes)
        {
            var length = Math.Min(frameBytes, pcm.Length - offset);
            ticks.Add(await segmenter.PushAsync(pcm.Slice(offset, length), CancellationToken.None));
        }

        return ticks;
    }

    private static string Ranges(IEnumerable<LiveTick> ticks) =>
        string.Join(';', ticks.SelectMany(tick => tick.Commits).Select(commit => $"[{commit.StartMs},{commit.EndMs}]{commit.Text}"));

    private static void AssertCoversWithoutAHole(IReadOnlyList<SubmittedWindow> windows, long untilMs, string message)
    {
        var covered = 0L;
        foreach (var window in windows.OrderBy(window => window.StartMs).ThenBy(window => window.EndMs))
        {
            if (window.StartMs > covered)
            {
                break;
            }

            covered = Math.Max(covered, window.EndMs);
        }

        AssertEx.True(covered >= untilMs, $"{message} Submitted audio is contiguous only up to {covered} ms, but the watermark is at {untilMs} ms.");
    }
}

/// <summary>An absolute range of session audio that reached the transcriber, decoded from the submitted bytes.</summary>
internal sealed record SubmittedWindow(long StartMs, long EndMs)
{
    public long DurationMs => EndMs - StartMs;

    public override string ToString() =>
        $"[{StartMs},{EndMs})";
}

/// <summary>
///     Answers each submitted window from a caller-supplied script and records what it was handed.
/// </summary>
/// <remarks>
///     Hand-written rather than substituted for the same reason as <see cref="RecordedWhisperTranscriber" />: the
///     response depends on the submitted audio, and decoding that audio back into an absolute range is the assertion
///     these tests are built on, not an incidental detail a call handler could hide.
/// </remarks>
internal sealed class ScriptedWhisperTranscriber : IWhisperTranscriber
{
    private readonly List<bool> _detectLanguageFlags = [];
    private readonly List<SubmittedWindow> _windows = [];
    private readonly Func<SubmittedWindow, IReadOnlyList<WhisperTranscriptSegment>> _respond;

    public ScriptedWhisperTranscriber(Func<SubmittedWindow, IReadOnlyList<WhisperTranscriptSegment>> respond)
    {
        _respond = respond;
    }

    /// <summary>Thrown instead of answering, when set.</summary>
    public Exception? Failure { get; set; }

    /// <summary>The code every answer reports, or <see langword="null" /> to report none.</summary>
    public string? DetectedLanguageCode { get; set; }

    public IReadOnlyList<SubmittedWindow> Windows => _windows;

    /// <summary>Whether each call asked for the language probabilities, in call order.</summary>
    public IReadOnlyList<bool> DetectLanguageFlags => _detectLanguageFlags;

    public int CallCount => _windows.Count;

    public async Task<WhisperTranscriptionResult> TranscribeAsync(string modelId, WhisperTranscriptionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        AssertEx.True(request.Audio.CanSeek, "The transcriber is handed a seekable stream.");
        AssertEx.Equal("audio/wav", request.ContentType, "The live path submits WAV.");
        AssertEx.True(request.UseVoiceActivityDetection, "A live window is always submitted with VAD on.");

        using var buffer = new MemoryStream();
        await request.Audio.CopyToAsync(buffer, ct);
        request.Audio.Seek(offset: 0, SeekOrigin.Begin);

        var window = LivePcm.Decode(WavPayload.Read(buffer.ToArray()));
        _windows.Add(window);
        _detectLanguageFlags.Add(request.DetectLanguage);

        if (Failure is not null)
        {
            throw Failure;
        }

        var segments = _respond(window);
        return new WhisperTranscriptionResult
        {
            Text = string.Join(' ', segments.Select(segment => segment.Text)).Trim(),
            Segments = segments,
            DetectedLanguageCode = DetectedLanguageCode,
            DetectedLanguageProbability = DetectedLanguageCode is null ? null : 0.99,
            DurationSeconds = window.DurationMs / 1000.0
        };
    }
}

/// <summary>Synthetic PCM whose every millisecond names itself, so a submitted window decodes back to its own range.</summary>
internal static class LivePcm
{
    /// <summary>Builds the raw PCM for session audio time <paramref name="fromMs" /> up to <paramref name="toMs" />.</summary>
    public static ReadOnlyMemory<byte> Range(long fromMs, long toMs)
    {
        var pcm = new byte[(toMs - fromMs) * 32];
        for (var index = 0; index < pcm.Length / 32; index++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(pcm.AsSpan(index * 32), fromMs + index);
        }

        return pcm;
    }

    /// <summary>Recovers the absolute range a payload carries, failing if its milliseconds are not contiguous.</summary>
    public static SubmittedWindow Decode(ReadOnlyMemory<byte> payload)
    {
        AssertEx.True(payload.Length > 0 && payload.Length % 32 == 0,
            $"A submitted window is a whole number of milliseconds; this one was {payload.Length} bytes.");

        var span = payload.Span;
        var startMs = BinaryPrimitives.ReadInt64LittleEndian(span);
        for (var index = 1; index < payload.Length / 32; index++)
        {
            AssertEx.Equal(startMs + index,
                BinaryPrimitives.ReadInt64LittleEndian(span[(index * 32)..]),
                "The submitted audio must be one contiguous run; a gap means the buffer was spliced wrongly.");
        }

        return new SubmittedWindow(startMs, startMs + (payload.Length / 32));
    }
}
