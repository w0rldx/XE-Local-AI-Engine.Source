namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;
using XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

/// <summary>
///     The live-capture chain, end to end, in a real browser: Chromium's fake microphone plays the repository's own
///     speech fixture, the SPA's worklet resamples it to 16 kHz mono int16, the frames travel base64 over the SignalR
///     hub, the real segmenter and registry turn them into a committed segment, and the committed segment survives a
///     reload because it was persisted rather than only pushed.
///     <para>
///         Only the model is faked. What the fake asserts about the audio it is handed — that its envelope matches the
///         fixture's — is what stops this test going green on Chromium's default beep, and the tone control in
///         <see cref="LiveTranscriptionProvenanceControlE2ETests" /> is what proves that check is alive.
///     </para>
///     <para>
///         Serial: a live session holds node-global transcription state, and the fake transcriber's provenance signal
///         is a single shared instance re-armed before each test.
///     </para>
/// </summary>
[Category("Page")]
public sealed class LiveTranscriptionE2ETests : XEFakeAudioE2ETestBase
{
    public LiveTranscriptionE2ETests() : base(FakeAudioFixtures.JfkWavPath)
    {
    }

    [Test]
    [Category("Page")]
    public async Task StartingAMicrophoneSession_CommitsASegmentFromTheFakeMicrophone()
    {
        var elapsed = Stopwatch.StartNew();
        await StartLiveMicrophoneSessionAsync();

        await Expect(FakeAudioPage.GetByTestId("transcription-live-panel")).ToBeVisibleAsync();
        try
        {
            await Expect(FakeAudioPage.GetByTestId("transcription-committed-list")).ToContainTextAsync("fellow Americans", new()
            {
                Timeout = FirstCommitBudgetMs
            });
        }
        finally
        {
            // The wall time to the first commit and the correlation that accepted it are the two numbers the slice
            // report quotes; the threshold is calibrated from them, not from the plan's initial guess. Written on the
            // failure path too: "no commit" has three causes (no audio reached the node, the provenance check
            // rejected it, the commit never rendered) and the measured correlation is what tells them apart.
            await RecordDiagnosticAsync(
                $"first commit after {elapsed.ElapsedMilliseconds} ms, correlation={Transcriber.LastCorrelation?.ToString(CultureInfo.InvariantCulture) ?? "(none measured)"}, nonSilentInputSeen={Transcriber.FirstNonSilentInput.Task.IsCompleted}");
        }

        await FakeAudioPage.GetByTestId("transcription-capture-stop").ClickAsync();
        await Expect(FakeAudioPage.GetByTestId("transcription-session-status")).ToContainTextAsync("Completed", new()
        {
            Timeout = FirstCommitBudgetMs
        });

        // The reload is the persistence assertion: transcript-segment-list is S2's REST-fed list, fed by the stored
        // rows, not by anything this page kept in memory.
        await FakeAudioPage.ReloadAsync(new()
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });
        await Expect(FakeAudioPage.GetByTestId("transcript-segment-list")).ToContainTextAsync("fellow Americans", new()
        {
            Timeout = FirstCommitBudgetMs
        });
    }
}

/// <summary>
///     The negative control for <see cref="LiveTranscriptionE2ETests" />. Chromium plays an unrelated 440 Hz tone, and
///     the test asserts the fake transcriber <b>measured</b> that audio and rejected it on its merits.
///     <para>
///         It is a separate class because the WAV is a browser command-line switch, so it is fixed for the lifetime of
///         a launched browser: <see cref="XEFakeAudioE2ETestBase" /> launches one per test, and each suite declares
///         its own file once, in its constructor.
///     </para>
///     <para>
///         Awaiting the fake's first-non-silent-input signal is the load-bearing step. "Nothing was committed" on its
///         own also passes when capture never started, when the worklet threw, when the transport failed, or when the
///         fake was never called — elapsed silence is never the evidence here.
///     </para>
/// </summary>
[Category("Page")]
public sealed class LiveTranscriptionProvenanceControlE2ETests : XEFakeAudioE2ETestBase
{
    public LiveTranscriptionProvenanceControlE2ETests() : base(FakeAudioFixtures.ToneWavPath)
    {
    }

    [Test]
    [Category("Page")]
    public async Task StartingAMicrophoneSession_WithUnrelatedAudio_IsRejectedByTheProvenanceCheck()
    {
        await StartLiveMicrophoneSessionAsync();
        await Expect(FakeAudioPage.GetByTestId("transcription-live-panel")).ToBeVisibleAsync();

        // real-timer: the signal is completed by the host on a real browser's audio, so the only bound available is
        // wall time. It is the same budget the positive case gives the first commit.
        var correlation = await Transcriber.FirstNonSilentInput.Task
                                           .WaitAsync(TimeSpan.FromMilliseconds(FirstCommitBudgetMs));

        await RecordDiagnosticAsync($"tone control correlation={correlation.ToString(CultureInfo.InvariantCulture)}");
        await Assert.That(correlation).IsLessThan(FakeJfkWhisperTranscriber.CorrelationThreshold);

        await FakeAudioPage.GetByTestId("transcription-capture-stop").ClickAsync();

        // Completed is what ending a session reaches; the alternative terminal states are accepted because what is
        // under test here is the provenance verdict, not which terminal state a stopped session settles on.
        await Expect(FakeAudioPage.GetByTestId("transcription-session-status"))
            .ToContainTextAsync(new Regex("Completed|Cancelled|Failed"), new()
            {
                Timeout = FirstCommitBudgetMs
            });

        await FakeAudioPage.ReloadAsync(new()
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });
        await Expect(FakeAudioPage.GetByTestId(new Regex(@"^transcript-segment-\d+$"))).ToHaveCountAsync(0);
    }
}
