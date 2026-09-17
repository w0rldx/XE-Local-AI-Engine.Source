namespace XE_Local_AI_Engine.Tests.E2ETests.Common;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

/// <summary>
///     Serial base for the browser tests that drive Chromium's fake audio device. The switches replace the microphone
///     with a WAV file, and auto-accept the permission prompt, so the whole live-capture chain runs headless with no
///     hardware and no dialog handling.
///     <para>
///         The file is played <b>looping</b> (Chromium's <c>--use-file-for-fake-audio-capture</c> stops at the end only
///         with a <c>%noloop</c> suffix, which is deliberately not used). A looping source keeps producing audio when
///         the host is slow, which is the difference between a slow test and a flaky one — so a derived test waits for
///         the first matching commit and then stops the session itself, rather than waiting for the stream to end.
///     </para>
///     <para>
///         The fake device replaces the <b>microphone only</b>. Headless Chromium has no equivalent fake for
///         <c>getDisplayMedia</c>, so the system-audio path is covered by the frontend unit tests and the manual live
///         check, never here. That is a real coverage boundary, not an oversight.
///     </para>
///     <para>
///         <b>This base launches its own browser</b> and drives its own page. It cannot use the harness's shared one:
///         TUnit.Playwright caches ONE browser per worker under a fixed service key, so the first class to launch on a
///         worker fixes the command line and every later class's <see cref="BrowserTypeLaunchOptions" /> is silently
///         ignored — which for a per-class WAV switch means the second suite captures the FIRST suite's audio. The
///         harness's shared <c>Page</c> and <c>Context</c> are left untouched and unused here.
///     </para>
/// </summary>
// S101: matches the XEE2ETestBase harness naming; see that type for why the prefix is intentional.
#pragma warning disable S101 // Types should be named in PascalCase
public abstract class XEFakeAudioE2ETestBase : XESerialE2ETestBase
{
    /// <summary>
    ///     How long the first committed segment may take: the browser group is contended and the segmenter needs a
    ///     full window plus the tail guard before it can commit anything. Same reasoning as
    ///     XE-Local-AI-Engine.Tests/Testing/TestBudgets.Contended, which this project cannot reference — TestBudgets
    ///     is internal to XE_Local_AI_Engine.Tests.Testing.
    /// </summary>
    public const int FirstCommitBudgetMs = 30_000;

    private readonly string _wavPath;

    private IBrowser? _fakeAudioBrowser;
    private IBrowserContext? _fakeAudioContext;
    private IPage? _fakeAudioPage;
    private bool _fakeAudioTracing;

    /// <param name="wavPath">The WAV the fake microphone plays. 16 kHz mono 16-bit; it must exist.</param>
    protected XEFakeAudioE2ETestBase(string wavPath)
    {
        // Checked before the browser launches: a missing fixture must fail with this sentence, not with an empty
        // transcript thirty seconds later.
        if (!File.Exists(wavPath))
        {
            throw new FileNotFoundException($"The fake-audio fixture is missing, so the browser would capture silence: {wavPath}", wavPath);
        }

        _wavPath = wavPath;
    }

    /// <summary>The fake transcriber the host answers with, so a test can await its provenance signal.</summary>
    private protected FakeJfkWhisperTranscriber Transcriber => Factory.Services.GetRequiredService<FakeJfkWhisperTranscriber>();

    /// <summary>
    ///     The page every test in a fake-audio suite drives. It belongs to this test's own browser — the one whose
    ///     command line actually carries this suite's WAV — never to the harness's shared, per-worker browser.
    /// </summary>
    protected IPage FakeAudioPage =>
        _fakeAudioPage ?? throw new InvalidOperationException($"{nameof(FakeAudioPage)} is only available inside a test: {nameof(LaunchFakeAudioBrowserAsync)} creates it in a [Before(Test)] hook.");

    /// <summary>
    ///     Re-arms the fake transcriber's first-non-silent-input signal. The host is shared for the whole test session,
    ///     so without this the second audio test would await a signal the first one already completed.
    /// </summary>
    [Before(Test)]
    public void ResetFakeTranscriber() =>
        Transcriber.ResetForTests();

    /// <summary>
    ///     Nothing here reads the harness's shared page, so signing it in would cost a navigation and a login per test
    ///     for a page no assertion touches. <see cref="LaunchFakeAudioBrowserAsync" /> signs in on
    ///     <see cref="FakeAudioPage" /> instead, through the same
    ///     <see cref="XESerialE2ETestBase.SignInWithFormAsync" /> steps. The shared context is left as the harness
    ///     created it — untouched, not half-initialised.
    /// </summary>
    protected override Task SignInAsync() =>
        Task.CompletedTask;

    /// <summary>
    ///     Launches this test's own Chromium with the fake-audio switches on <b>its</b> command line, opens a context
    ///     and a page on it, starts tracing, and signs in.
    /// </summary>
    /// <remarks>
    ///     TUnit.Playwright caches one browser per worker under a fixed service key, so the launch options a class
    ///     passes through its constructor are honoured only for the FIRST class that launches on that worker. Measured
    ///     across three serial runs of the two audio suites: whichever class ran second was fed the first one's audio —
    ///     the fixture correlated 0.951 when it ran first and 0 when it ran second, and the tone control 0.76 when
    ///     first and 0.99 when second. A per-class WAV therefore cannot travel through the shared browser at all.
    ///     <para>
    ///         Base hooks run before derived ones, so <c>Playwright</c> is already set up when this runs.
    ///     </para>
    /// </remarks>
    [Before(Test)]
    public async Task LaunchFakeAudioBrowserAsync(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _fakeAudioBrowser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = LaunchHeadless,
            Args =
            [
                "--ignore-certificate-errors",
                "--use-fake-device-for-media-stream",
                "--use-fake-ui-for-media-stream",
                $"--use-file-for-fake-audio-capture={_wavPath}"
            ]
        });

        _fakeAudioContext = await _fakeAudioBrowser.NewContextAsync(ContextOptions(context));
        await _fakeAudioContext.Tracing.StartAsync(new TracingStartOptions
        {
            Screenshots = true,
            Snapshots = true,
            Sources = true,
            Title = $"{GetType().Name}.{context.Metadata.TestName}.fake-audio"
        });
        _fakeAudioTracing = true;

        _fakeAudioPage = await _fakeAudioContext.NewPageAsync();
        await SignInWithFormAsync(_fakeAudioPage, NodeAppUrl);
    }

    /// <summary>
    ///     Writes the trace on failure and closes this test's own browser. The <c>_fake-audio</c> infix keeps the zip
    ///     apart from the one the harness writes for the shared context, which carries the same class and test name.
    /// </summary>
    [After(Test)]
    public async Task DisposeFakeAudioBrowserAsync(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_fakeAudioContext is not null && _fakeAudioTracing)
        {
            var failed = context.Execution.Result?.State.ToString() is "Failed" or "Errored";
            if (failed)
            {
                var traceDirectory = Path.Combine("test-results", "traces");
                Directory.CreateDirectory(traceDirectory);
                await _fakeAudioContext.Tracing.StopAsync(new TracingStopOptions
                {
                    Path = Path.Combine(traceDirectory,
                        $"{GetType().Name}_{context.Metadata.TestName}_fake-audio_{DateTime.UtcNow:yyyyMMdd_HHmmss}.zip")
                });
            }
            else
            {
                await _fakeAudioContext.Tracing.StopAsync(new TracingStopOptions());
            }

            _fakeAudioTracing = false;
        }

        _fakeAudioPage = null;

        if (_fakeAudioContext is not null)
        {
            await _fakeAudioContext.CloseAsync();
            _fakeAudioContext = null;
        }

        if (_fakeAudioBrowser is not null)
        {
            await _fakeAudioBrowser.CloseAsync();
            _fakeAudioBrowser = null;
        }
    }

    /// <summary>
    ///     Writes one measurement line to the console AND to <c>test-results/live-transcription-diag.log</c> under the
    ///     test output directory: the platform only surfaces a passing test's console, so the numbers a report quotes
    ///     from a green run would otherwise be unreachable.
    /// </summary>
    protected static async Task RecordDiagnosticAsync(string line)
    {
        var stamped = $"[DIAG] {DateTimeOffset.UtcNow:O} {line}";
        await Console.Out.WriteLineAsync(stamped);
        var directory = Path.Combine(AppContext.BaseDirectory, "test-results");
        Directory.CreateDirectory(directory);
        await File.AppendAllTextAsync(Path.Combine(directory, "live-transcription-diag.log"), stamped + Environment.NewLine);
    }

    /// <summary>
    ///     Drives the shipped two-step flow to a live microphone session: create the session from the list page, then
    ///     press Start on the session page. Both audio suites take the same route, and the only thing that differs
    ///     between them is which WAV the browser is playing.
    /// </summary>
    protected async Task StartLiveMicrophoneSessionAsync()
    {
        await FakeAudioPage.GotoAsync($"{NodeAppUrl}/transcription", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await FakeAudioPage.GetByTestId("transcription-create").ClickAsync();
        await Expect(FakeAudioPage.GetByTestId("new-transcription-session-dialog")).ToBeVisibleAsync();

        // Mantine's SegmentedControl keeps its radio inputs visually hidden and paints the label, so Playwright's
        // actionability check refuses the input ("element is not visible"); the label is what a person clicks.
        // Exact: "Microphone + system" is the other option whose label starts with the same word.
        await FakeAudioPage.GetByTestId("new-transcription-session-source")
                           .GetByText("Microphone", new LocatorGetByTextOptions
                           {
                               Exact = true
                           })
                           .ClickAsync();

        await FakeAudioPage.GetByTestId("new-transcription-session-submit").ClickAsync();

        // Submitting navigates to /transcription/{sessionId}; capture starts from the session page, not the dialog.
        await FakeAudioPage.WaitForURLAsync($"{NodeAppUrl}/transcription/*");
        await FakeAudioPage.GetByTestId("transcription-capture-start").ClickAsync();
    }
}
#pragma warning restore S101
