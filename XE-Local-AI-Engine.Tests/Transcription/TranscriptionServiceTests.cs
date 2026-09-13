namespace XE_Local_AI_Engine.Tests.Transcription;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers the batch file-transcription path end to end against the real session store: what reaches the runtime,
///     what is refused before it, and — on every one of those routes — that no audio is left on disk afterwards.
/// </summary>
/// <remarks>
///     The temp-directory assertion is repeated per outcome on purpose. The rule these tests defend is that exactly one
///     object owns the uploaded file, so the interesting question is never "does the happy path clean up" but "does the
///     failure that nobody thought about clean up".
/// </remarks>
public sealed class TranscriptionServiceTests
{
    [Test]
    [Arguments("99")]
    [Arguments("-1")]
    [Arguments("Telepathy")]
    public async Task CreateSession_WithNumericSourceKind_Throws(string sourceKind)
    {
        // Matching on NAMES rather than through Enum.TryParse is the whole point: TryParse also parses the underlying
        // values, so "99" would have been stored as an ordinal no member has and read back on the wire as "99". The
        // endpoint validator refuses the same strings, but the service is the layer that writes the row, so the rule
        // has to hold here too — nothing guarantees this service is only ever reached through that endpoint.
        await using var harness = await TranscriptionServiceHarness.CreateAsync();

        _ = await AssertEx.ThrowsAsync<ArgumentException>(() => harness.Service.CreateSessionAsync(new CreateTranscriptionSessionInput
            {
                SourceKind = sourceKind
            },
            CancellationToken.None));
    }

    [Test]
    public async Task CreateSession_WithANamedSourceKind_IsAccepted()
    {
        // The control: without it the table above would pass against a parse that rejects everything.
        await using var harness = await TranscriptionServiceHarness.CreateAsync();

        var session = await harness.Service.CreateSessionAsync(new CreateTranscriptionSessionInput
            {
                SourceKind = "microphone"
            },
            CancellationToken.None);

        AssertEx.Equal(TranscriptionSourceKind.Microphone, session.SourceKind);
    }

    [Test]
    public async Task TranscribeFile_WhenSucceeded_LeavesTempDirectoryEmpty()
    {
        await using var harness = await TranscriptionServiceHarness.CreateAsync();
        harness.Transcriber.Result = new WhisperTranscriptionResult("hello there",
            [
                new WhisperTranscriptSegment(0.0, 1.5, "hello", 0.8),
                new WhisperTranscriptSegment(1.5, 2.25, "there", 0.7)
            ],
            "en",
            0.99,
            2.25);

        var (sessionId, slot) = await harness.BeginAsync(TranscriptionAudioFixtures.Wav);
        TranscribeFileResult result;
        await using (slot)
        {
            result = await harness.Service.TranscribeFileAsync(slot, CancellationToken.None);
        }

        AssertEx.Equal(TranscribeFileOutcome.Succeeded, result.Outcome, result.ErrorMessage);
        AssertEx.Empty(harness.TempFiles, "The upload slot must delete the audio it owned.");

        var session = AssertEx.NotNull(result.Session);
        AssertEx.Equal(TranscriptionSessionStatus.Completed, session.Status);
        AssertEx.Equal("en", session.DetectedLanguage);
        AssertEx.Equal(2250L, session.DurationMs, "Fractional seconds convert to whole milliseconds at the boundary.");
        AssertEx.Equal(2, session.Segments.Count);

        // Sequence numbers start at 1, never 0, so "no segments yet" and "segment zero" cannot be confused.
        AssertEx.Equal(1L, session.Segments[0].Seq);
        AssertEx.Equal(2L, session.Segments[1].Seq);
        AssertEx.Equal(0L, session.Segments[0].StartMs);
        AssertEx.Equal(1500L, session.Segments[0].EndMs);
        AssertEx.Equal(2250L, session.Segments[1].EndMs);
        AssertEx.Equal(TranscriptChannel.Mono, session.Segments[0].Channel);

        AssertEx.Equal(1, harness.Transcriber.CallCount);
        AssertEx.Equal(0L, harness.Transcriber.LastStartPosition, "The audio stream must be rewound before each call.");
        AssertEx.Equal(TranscriptionServiceHarness.EffectiveModelId, harness.Transcriber.LastModelId);
        AssertEx.Contains(harness.Supervisor.EnsureRunningCalls, TranscriptionServiceHarness.EffectiveModelId);

        // The session still exists for a later read; only the audio is gone.
        var reread = AssertEx.NotNull(await harness.Service.GetSessionAsync(sessionId, CancellationToken.None));
        AssertEx.Equal(2, reread.Segments.Count);
    }

    [Test]
    public async Task TranscribeFile_WhenTranscriberThrows_LeavesTempDirectoryEmpty()
    {
        await using var harness = await TranscriptionServiceHarness.CreateAsync();
        harness.Transcriber.Failure = new WhisperRuntimeException("The runtime rejected the audio.");

        var (sessionId, slot) = await harness.BeginAsync(TranscriptionAudioFixtures.Wav);
        TranscribeFileResult result;
        await using (slot)
        {
            result = await harness.Service.TranscribeFileAsync(slot, CancellationToken.None);
        }

        AssertEx.Equal(TranscribeFileOutcome.RuntimeFailed, result.Outcome);
        AssertEx.Equal("runtime-failed", result.ErrorCode);
        AssertEx.Empty(harness.TempFiles, "A failed transcription must not leave the audio behind.");

        var session = AssertEx.NotNull(await harness.Service.GetSessionAsync(sessionId, CancellationToken.None));
        AssertEx.Equal(TranscriptionSessionStatus.Failed, session.Status);
        AssertEx.Equal("runtime-failed", session.ErrorCode);
        AssertEx.Equal("The runtime rejected the audio.", session.ErrorMessage);
    }

    [Test]
    public async Task TranscribeFile_WhenCancelled_LeavesTempDirectoryEmpty()
    {
        await using var harness = await TranscriptionServiceHarness.CreateAsync();

        // A gate the test releases, never a sleep: the transcription parks inside the fake until this completes.
        harness.Transcriber.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (sessionId, slot) = await harness.BeginAsync(TranscriptionAudioFixtures.Wav);
        TranscribeFileResult result;
        await using (slot)
        {
            var running = harness.Service.TranscribeFileAsync(slot, CancellationToken.None);
            await harness.Transcriber.Entered;

            AssertEx.True(await harness.Service.CancelAsync(sessionId, CancellationToken.None),
                "A session with a transcription in flight must be cancellable.");
            harness.Transcriber.Gate.TrySetResult();
            result = await running;
        }

        AssertEx.Equal(TranscribeFileOutcome.Cancelled, result.Outcome);
        AssertEx.Empty(harness.TempFiles, "A cancelled transcription must not leave the audio behind.");

        var session = AssertEx.NotNull(await harness.Service.GetSessionAsync(sessionId, CancellationToken.None));
        AssertEx.Equal(TranscriptionSessionStatus.Cancelled, session.Status);

        AssertEx.False(await harness.Service.CancelAsync(sessionId, CancellationToken.None),
            "Nothing is in flight once the transcription has returned.");
    }

    [Test]
    public async Task TranscribeFile_WhenOggAndFfmpegAbsent_ReturnsUnsupportedContainerListingSupported()
    {
        await using var harness = await TranscriptionServiceHarness.CreateAsync();
        harness.Transcoder.IsAvailable = false;

        // The client called it a WAV. The bytes say otherwise, and the bytes are what decide.
        var (_, slot) = await harness.BeginAsync(TranscriptionAudioFixtures.Ogg, ".wav");
        TranscribeFileResult result;
        await using (slot)
        {
            result = await harness.Service.TranscribeFileAsync(slot, CancellationToken.None);
        }

        AssertEx.Equal(TranscribeFileOutcome.UnsupportedContainer, result.Outcome);
        AssertEx.Equal(AudioContainer.Ogg, result.DetectedContainer, "The extension must never be trusted over the bytes.");
        AssertEx.Equal("wav,mp3,flac", string.Join(',', result.SupportedContainers));
        AssertEx.Contains(result.ErrorMessage, "ogg", StringComparison.Ordinal);
        AssertEx.True(result.FfmpegRequired, "The refusal must say the container would work with ffmpeg installed.");
        AssertEx.Contains(result.ErrorMessage, "ffmpeg", StringComparison.OrdinalIgnoreCase);
        AssertEx.Empty(harness.TempFiles, "A refused upload must not leave the audio behind.");
    }

    [Test]
    public async Task TranscribeFile_WhenOggAndFfmpegPresent_TranscodesThenTranscribes()
    {
        await using var harness = await TranscriptionServiceHarness.CreateAsync();
        harness.Transcoder.IsAvailable = true;

        var (_, slot) = await harness.BeginAsync(TranscriptionAudioFixtures.Ogg, ".ogg");
        TranscribeFileResult result;
        await using (slot)
        {
            result = await harness.Service.TranscribeFileAsync(slot, CancellationToken.None);
        }

        AssertEx.Equal(TranscribeFileOutcome.Succeeded, result.Outcome, result.ErrorMessage);
        AssertEx.Equal(1, harness.Transcoder.Calls.Count);
        AssertEx.Equal("audio/wav", harness.Transcriber.LastContentType, "Only WAV reaches the runtime on this route.");
        AssertEx.True(harness.Transcriber.LastAudioHead.AsSpan().SequenceEqual(TranscriptionAudioFixtures.TranscodedWav.AsSpan(0, harness.Transcriber.LastAudioHead.Length)),
            "The runtime must receive the converted file, not the original Ogg.");
        AssertEx.Empty(harness.TempFiles, "Both the original and the converted file are the slot's to delete.");
    }

    [Test]
    public async Task TranscribeFile_WhenNativeContainer_NeverCallsTheTranscoder()
    {
        await using var harness = await TranscriptionServiceHarness.CreateAsync();
        harness.Transcoder.IsAvailable = true;

        var (_, slot) = await harness.BeginAsync(TranscriptionAudioFixtures.Flac, ".flac");
        TranscribeFileResult result;
        await using (slot)
        {
            result = await harness.Service.TranscribeFileAsync(slot, CancellationToken.None);
        }

        AssertEx.Equal(TranscribeFileOutcome.Succeeded, result.Outcome, result.ErrorMessage);
        AssertEx.Empty(harness.Transcoder.Calls, "A natively decodable container must reach the runtime untouched.");
        AssertEx.Equal("audio/flac", harness.Transcriber.LastContentType);
    }

    [Test]
    public async Task TranscribeFile_WhenTranscodeFails_LeavesTempDirectoryEmpty()
    {
        await using var harness = await TranscriptionServiceHarness.CreateAsync();
        harness.Transcoder.IsAvailable = true;

        // The converter writes a partial file and then fails. That partial file is audio, and it must go too.
        harness.Transcoder.ThrowAfterPartialWrite = true;

        var (sessionId, slot) = await harness.BeginAsync(TranscriptionAudioFixtures.Matroska, ".webm");
        TranscribeFileResult result;
        await using (slot)
        {
            result = await harness.Service.TranscribeFileAsync(slot, CancellationToken.None);
            AssertEx.Equal(2, harness.TempFiles.Count, "Both the upload and the half-written conversion exist before disposal.");
        }

        AssertEx.Equal(TranscribeFileOutcome.RuntimeFailed, result.Outcome);
        AssertEx.Equal("transcode-failed", result.ErrorCode);
        AssertEx.Empty(harness.TempFiles, "Both owned paths must be gone after one disposal.");

        var session = AssertEx.NotNull(await harness.Service.GetSessionAsync(sessionId, CancellationToken.None));
        AssertEx.Equal(TranscriptionSessionStatus.Failed, session.Status);
        AssertEx.Equal("transcode-failed", session.ErrorCode);
        AssertEx.Equal(0, harness.Transcriber.CallCount, "A failed conversion must never reach the runtime.");
    }

    // Every row here is a session option that only matters if it survives the trip through the encrypted config
    // column and into the runtime request. The fake records what it was handed; nothing else can prove the mapping.
    [Test]
    [Arguments("override", "de", false, WhisperLanguageMode.Explicit, "de", false, "an explicit language is forwarded verbatim")]
    [Arguments("auto", null, false, WhisperLanguageMode.Auto, null, false, "auto detection carries no code")]
    [Arguments("auto", null, true, WhisperLanguageMode.Auto, null, true, "translation is forwarded")]
    [Arguments("override", null, false, WhisperLanguageMode.Auto, null, false, "override without a code degrades to auto")]
    [Arguments("override", "   ", false, WhisperLanguageMode.Auto, null, false, "override with a blank code degrades to auto")]
    public async Task TranscribeFile_MapsTheSessionLanguageOptionsOntoTheRuntimeRequest(string languageMode,
        string? languageOverride,
        bool translate,
        WhisperLanguageMode expectedMode,
        string? expectedCode,
        bool expectedTranslate,
        string because)
    {
        await using var harness = await TranscriptionServiceHarness.CreateAsync();

        var (_, slot) = await harness.BeginAsync(TranscriptionAudioFixtures.Wav,
            ".wav",
            new TranscriptionSessionConfig
            {
                LanguageMode = languageMode,
                LanguageOverride = languageOverride,
                Translate = translate
            });

        TranscribeFileResult result;
        await using (slot)
        {
            result = await harness.Service.TranscribeFileAsync(slot, CancellationToken.None);
        }

        AssertEx.Equal(TranscribeFileOutcome.Succeeded, result.Outcome, result.ErrorMessage);
        AssertEx.Equal(expectedMode, harness.Transcriber.LastLanguageMode, $"Expected {expectedMode} because {because}.");
        AssertEx.Equal<string?>(expectedCode, harness.Transcriber.LastLanguageCode, $"Expected language code '{expectedCode}' because {because}.");
        AssertEx.Equal(expectedTranslate, harness.Transcriber.LastTranslate, $"Expected translate={expectedTranslate} because {because}.");
    }

    [Test]
    public async Task TranscribeFile_WhenAlreadyTranscribing_RefusesTheSecondCallWithoutWritingTheSession()
    {
        await using var harness = await TranscriptionServiceHarness.CreateAsync();
        harness.Transcriber.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (sessionId, firstSlot) = await harness.BeginAsync(TranscriptionAudioFixtures.Wav);
        await using (firstSlot)
        {
            var running = harness.Service.TranscribeFileAsync(firstSlot, CancellationToken.None);
            await harness.Transcriber.Entered;

            // A second upload for the same session, while the first is parked inside the runtime.
            var secondSlot = await harness.Service.BeginUploadAsync(sessionId, ".wav", CancellationToken.None);
            TranscribeFileResult refused;
            await using (secondSlot)
            {
                await File.WriteAllBytesAsync(secondSlot.SourcePath, TranscriptionAudioFixtures.Wav);
                var second = harness.Service.TranscribeFileAsync(secondSlot, CancellationToken.None);

                // real-timer: a bound on the refusal, not a wait for an event. Without the in-flight guard the second
                // call reaches the runtime and parks on the same gate the first one holds, and this test would hang
                // rather than fail — a regression that stalls CI instead of reporting.
                await AssertEx.CompletesAsync(second,
                    TimeSpan.FromSeconds(30),
                    "The second call must be refused immediately, never queued behind the running transcription.");
                refused = await second;
            }

            AssertEx.Equal(TranscribeFileOutcome.RuntimeFailed, refused.Outcome);
            AssertEx.Equal("already-transcribing", refused.ErrorCode);

            // The refusal must not touch the row: the first run still owns it and is about to complete it.
            var duringRefusal = AssertEx.NotNull(await harness.Service.GetSessionAsync(sessionId, CancellationToken.None));
            AssertEx.Equal(TranscriptionSessionStatus.Transcribing, duringRefusal.Status);
            AssertEx.Null(duringRefusal.ErrorCode, "A refused second call must not record a failure on the session.");
            AssertEx.Equal(1, harness.Transcriber.CallCount, "The second call must never reach the runtime.");

            harness.Transcriber.Gate.TrySetResult();
            var first = await running;
            AssertEx.Equal(TranscribeFileOutcome.Succeeded, first.Outcome, first.ErrorMessage);
        }

        var completed = AssertEx.NotNull(await harness.Service.GetSessionAsync(sessionId, CancellationToken.None));
        AssertEx.Equal(TranscriptionSessionStatus.Completed, completed.Status);
        AssertEx.Empty(harness.TempFiles, "Both slots owned their own file and both are gone.");
    }

    /// <summary>
    ///     A second upload whose admission read straddles another run must never restart the session it finds.
    /// </summary>
    /// <remarks>
    ///     The interleaving is placed by hand, because it is the one the clock will not reproduce on demand: the
    ///     straggler's read of the session is held AFTER it returned, so it is left holding a snapshot that says
    ///     <c>Created</c> while the other upload transcribes the same session to completion. With the status read
    ///     before the in-flight guard was taken, the straggler then registered unopposed, moved a COMPLETED session
    ///     back to <c>Transcribing</c>, re-allocated sequence 1, and the unique <c>(session_id, seq)</c> index turned
    ///     the finished transcript into a failed one.
    ///     <para>
    ///         Which of the two uploads does the work is deliberately not asserted — that is exactly what taking the
    ///         guard first changes. What must hold either way is that the session is transcribed ONCE, ends
    ///         <c>Completed</c>, and that the call which lost writes nothing to the row.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task TranscribeFile_WhenSecondCallLandsAfterCompletion_DoesNotRestartTheSession()
    {
        await using var harness = await TranscriptionServiceHarness.CreateAsync();

        var (sessionId, straggler) = await harness.BeginAsync(TranscriptionAudioFixtures.Wav);
        TranscribeFileResult stragglerResult;
        TranscribeFileResult otherResult;

        await using (straggler)
        {
            harness.ReadGate.Arm();
            var stragglerRun = harness.Service.TranscribeFileAsync(straggler, CancellationToken.None);

            // real-timer: a bound on reaching the hold, so a change that stops reading the session at all reports a
            // deadline instead of hanging the run. A green run passes the moment the read is caught.
            await AssertEx.CompletesAsync(harness.ReadGate.Entered,
                TimeSpan.FromSeconds(30),
                "The straggler must reach its session read, which is where this test holds it.");

            var other = await harness.Service.BeginUploadAsync(sessionId, ".wav", CancellationToken.None);
            await using (other)
            {
                await File.WriteAllBytesAsync(other.SourcePath, TranscriptionAudioFixtures.Wav);
                otherResult = await harness.Service.TranscribeFileAsync(other, CancellationToken.None);
            }

            harness.ReadGate.Release();

            // real-timer: same bound, for the released call.
            await AssertEx.CompletesAsync(stragglerRun,
                TimeSpan.FromSeconds(30),
                "The held call must finish once the gate is released.");
            stragglerResult = await stragglerRun;
        }

        TranscribeFileResult[] results = [stragglerResult, otherResult];
        AssertEx.ContainsSingle(results,
            static result => result.Outcome == TranscribeFileOutcome.Succeeded,
            "Exactly one of the two uploads may transcribe the session.");
        AssertEx.ContainsSingle(results,
            static result => string.Equals(result.ErrorCode, "already-transcribing", StringComparison.Ordinal),
            "The upload that lost must be refused as already-transcribing, which is the only refusal that writes nothing.");
        AssertEx.Equal(1, harness.Transcriber.CallCount, "A session must reach the runtime once, never twice.");

        var session = AssertEx.NotNull(await harness.Service.GetSessionAsync(sessionId, CancellationToken.None));
        AssertEx.Equal(TranscriptionSessionStatus.Completed, session.Status,
            $"A completed session must not be restarted into a failure by a late arrival ({session.ErrorCode}: {session.ErrorMessage}).");
        AssertEx.Null(session.ErrorCode, "The refused call must leave no error on the finished session.");
        AssertEx.Equal(1, session.Segments.Count, "The transcript must hold one run's segments, with no duplicated sequence.");
        AssertEx.Empty(harness.TempFiles, "Both slots owned their own file and both are gone.");
    }

    [Test]
    public async Task TranscribeFile_WhenMatroskaAndFfmpegAbsent_NamesTheContainerAsWebm()
    {
        await using var harness = await TranscriptionServiceHarness.CreateAsync();
        harness.Transcoder.IsAvailable = false;

        var (_, slot) = await harness.BeginAsync(TranscriptionAudioFixtures.Matroska, ".webm");
        TranscribeFileResult result;
        await using (slot)
        {
            result = await harness.Service.TranscribeFileAsync(slot, CancellationToken.None);
        }

        AssertEx.Equal(TranscribeFileOutcome.UnsupportedContainer, result.Outcome);
        AssertEx.Equal(AudioContainer.Matroska, result.DetectedContainer, "The wire keeps the container's real name.");

        // The operator reads one vocabulary: the message must not say MATROSKA while the supported list says webm.
        AssertEx.Contains(result.ErrorMessage, "webm", StringComparison.Ordinal);
        AssertEx.False(result.ErrorMessage!.Contains("MATROSKA", StringComparison.OrdinalIgnoreCase),
            "The container name the operator never types must not appear in the message.");
    }

    [Test]
    public async Task TranscribeFile_WhenUnexpectedExceptionEscapes_MarksSessionFailed()
    {
        await using var harness = await TranscriptionServiceHarness.CreateAsync();

        // Not a WhisperRuntimeException and not an AudioTranscodeException: the kind of failure nobody enumerated.
        // The session is already Transcribing by this point, so without a catch-all it would stay there for good.
        harness.Transcriber.Failure = new InvalidOperationException("Something nobody enumerated went wrong.");

        var (sessionId, slot) = await harness.BeginAsync(TranscriptionAudioFixtures.Wav);
        TranscribeFileResult result;
        await using (slot)
        {
            result = await harness.Service.TranscribeFileAsync(slot, CancellationToken.None);
        }

        AssertEx.Equal(TranscribeFileOutcome.RuntimeFailed, result.Outcome);
        AssertEx.Equal("transcription-failed", result.ErrorCode);
        AssertEx.Empty(harness.TempFiles, "An unexpected failure must not leave the audio behind either.");

        var session = AssertEx.NotNull(await harness.Service.GetSessionAsync(sessionId, CancellationToken.None));
        AssertEx.Equal(TranscriptionSessionStatus.Failed, session.Status);
        AssertEx.Equal("transcription-failed", session.ErrorCode);
        AssertEx.Equal("The transcription failed.", session.ErrorMessage);
        AssertEx.False(session.ErrorMessage!.Contains("nobody enumerated", StringComparison.Ordinal),
            "The raw exception text must never reach the operator; only the sanitized message is persisted.");

        // Nothing is left registered, so the session can be retried rather than being permanently busy.
        AssertEx.False(await harness.Service.CancelAsync(sessionId, CancellationToken.None));
    }

    [Test]
    public async Task TranscribeFile_WhenContainerUnsupported_NeverCallsTheTranscriber()
    {
        await using var harness = await TranscriptionServiceHarness.CreateAsync();

        var (sessionId, slot) = await harness.BeginAsync([0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07], ".wav");
        TranscribeFileResult result;
        await using (slot)
        {
            result = await harness.Service.TranscribeFileAsync(slot, CancellationToken.None);
        }

        AssertEx.Equal(TranscribeFileOutcome.UnsupportedContainer, result.Outcome);
        AssertEx.Equal(AudioContainer.Unknown, result.DetectedContainer);
        AssertEx.False(result.FfmpegRequired, "Nothing this node could install would make these bytes transcribable.");
        AssertEx.Empty(harness.Supervisor.EnsureRunningCalls, "Sniffing must happen before the runtime is touched.");
        AssertEx.Equal(0, harness.Transcriber.CallCount);

        // The session is untouched: a refused upload is not a failed transcription, and the file can be replaced.
        var session = AssertEx.NotNull(await harness.Service.GetSessionAsync(sessionId, CancellationToken.None));
        AssertEx.Equal(TranscriptionSessionStatus.Created, session.Status);
    }
}
