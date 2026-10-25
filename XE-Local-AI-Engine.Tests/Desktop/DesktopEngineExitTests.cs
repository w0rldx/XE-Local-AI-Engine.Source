namespace XE_Local_AI_Engine.Tests.Desktop;

using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Desktop;
using XE_Local_AI_Engine.Tests.Testing;
using Program = XE_Local_AI_Engine.Client.Program;

/// <summary>How the shell explains an engine exit and when a failed page load is fatal. <c>[NotInParallel]</c> because
///     the notice text follows <see cref="DesktopText.Language" />, process-wide state these tests change and restore.</summary>
[Category(TestCategories.Unit)]
[NotInParallel]
public sealed class DesktopEngineExitTests
{
    private static readonly string BlockedTail =
        "Unhandled startup failure\n" + ApplicationControlBlock.Describe("Quartz.Serialization.SystemTextJson.dll") + "\n";

    [Test]
    public void StartupFailureExitCode_KeepsTheApplicationControlCodeOfAPreReadyEngineExit()
    {
        AssertEx.Equal(Program.ApplicationControlBlockedExitCode, DesktopApplication.ApplicationControlBlockedExitCode,
            "The shell's duplicate must stay the engine's code.");
        AssertEx.Equal(ApplicationControlBlock.StandardErrorPrefix, EngineExitNotice.BlockedPrefix,
            "The shell's prefix must stay the engine's.");
        AssertEx.Equal(11, DesktopApplication.StartupFailureExitCode(new DesktopEngineStartupException("engine", 11), engineReady: false));
    }

    [Test]
    public void RelaunchesEngine_OnlyOnTheEnginesRestartCode()
    {
        AssertEx.Equal(Program.RestartRequestedExitCode, DesktopApplication.RestartRequestedExitCode,
            "The shell's duplicate must stay the engine's code.");
        AssertEx.True(DesktopApplication.RelaunchesEngine(12));
        foreach (var other in new[]
                 {
                     0,
                     1,
                     8,
                     9,
                     10,
                     11,
                     -532462766
                 })
        {
            AssertEx.False(DesktopApplication.RelaunchesEngine(other), $"Exit {other} must keep its own meaning.");
        }
    }

    [Test]
    [Arguments("Quartz.Serialization.SystemTextJson.dll")]
    [Arguments("Quartz.Serialization.SystemTextJson, Version=3.22.6.0, Culture=neutral, PublicKeyToken=null")]
    [Arguments("an engine file")]
    public void BlockedFile_ReadsTheFileFromTheEnginesOwnReport(string file)
    {
        AssertEx.Equal(file, EngineExitNotice.BlockedFile(ApplicationControlBlock.Describe(file) + "\r\nnext line"));
        AssertEx.Equal(file, EngineExitNotice.BlockedFile("The engine exited before readiness (exit code 11). It reported: " + ApplicationControlBlock.Describe(file)),
            "The pre-ready failure quotes the report mid-line.");
    }

    [Test]
    public void BlockedFile_WithoutTheEnginesSuffixIsTheWholeRemainder() =>
        AssertEx.Equal("Quartz.dll. something else", EngineExitNotice.BlockedFile("Application control blocked: Quartz.dll. something else"));

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("Unhandled exception. System.IO.FileLoadException")]
    [Arguments("application control blocked: Quartz.dll. lower case is not the engine's report")]
    [Arguments("Application control blocked: ")]
    public void BlockedFile_IsNullWithoutTheEnginesReport(string? tail) =>
        AssertEx.Null(EngineExitNotice.BlockedFile(tail));

    [Test]
    public void For_NamesTheBlockedFileAndInstallFolderInBothLanguages()
    {
        var previous = DesktopText.Language;
        try
        {
            DesktopText.Apply("en");
            var english = EngineExitNotice.For(11, BlockedTail, "/opt/xe");
            AssertEx.Contains(english, "Smart App Control");
            AssertEx.Contains(english, "\"Quartz.Serialization.SystemTextJson.dll\"");
            AssertEx.Contains(english, "/opt/xe");
            AssertEx.Contains(english, "engine log in the logs folder, or in startup-crash.log if the engine stopped before its log opened");
            AssertEx.Contains(english, "not digitally signed");

            DesktopText.Apply("de");
            var german = EngineExitNotice.For(11, BlockedTail, "/opt/xe");
            AssertEx.Contains(german, "Smart App Control");
            AssertEx.Contains(german, "„Quartz.Serialization.SystemTextJson.dll“");
            AssertEx.Contains(german, "/opt/xe");
            AssertEx.Contains(german, "Engine-Protokoll im Protokollordner, oder in startup-crash.log, falls die Engine beendet wurde, bevor ihr Protokoll geöffnet war");
            AssertEx.Contains(german, "nicht digital signiert");
        }
        finally
        {
            DesktopText.Apply(previous);
        }
    }

    [Test]
    public void For_RecognisesTheBlockByExitCodeOrByTheReportAlone()
    {
        var previous = DesktopText.Language;
        try
        {
            DesktopText.Apply("en");
            AssertEx.Contains(EngineExitNotice.For(11, errorTail: null, "/opt/xe"), "a program file",
                message: "Exit code 11 without a report still names the block, just not the file.");
            AssertEx.Contains(EngineExitNotice.For(-532462766, BlockedTail, "/opt/xe"), "Quartz.Serialization.SystemTextJson.dll",
                message: "The report alone is enough when the runtime's own crash code replaced 11.");
            AssertEx.Equal(DesktopText.EngineStopped, EngineExitNotice.For(1, "Unhandled exception.", "/opt/xe"));
            AssertEx.Equal(DesktopText.EngineStopped, EngineExitNotice.For(10, errorTail: null, "/opt/xe"));
        }
        finally
        {
            DesktopText.Apply(previous);
        }
    }

    [Test]
    public void For_QuotesTheEnginesReportForAnOutOfRangeSetting()
    {
        var previous = DesktopText.Language;
        try
        {
            AssertEx.Equal(Program.OptionsValidationFailedExitCode, DesktopApplication.OptionsValidationFailedExitCode,
                "The shell's duplicate must stay the engine's code.");
            AssertEx.Equal(SettingValidationFailure.StandardErrorPrefix, EngineExitNotice.SettingPrefix, "The shell's prefix must stay the engine's.");

            DesktopText.Apply("en");
            const string Tail = "warn: an earlier stderr line\nSetting out of range: DevelopmentWorkspaceRetentionOptions: RetentionAge must be between 1 hour and 365 days.\n";
            AssertEx.Equal("A node setting is out of range, so the engine stopped. It reported: DevelopmentWorkspaceRetentionOptions: "
                           + "RetentionAge must be between 1 hour and 365 days. Fix the setting (environment variable or node-settings.json), then start XE again.",
                EngineExitNotice.For(13, Tail, "/opt/xe"), "Only the engine's report line is quoted, without its prefix.");
            foreach (var empty in new[]
                     {
                         null,
                         "",
                         " \n",
                         "Unhandled exception without the report",
                         "Setting out of range: "
                     })
            {
                var generic = EngineExitNotice.For(13, empty, "/opt/xe");
                AssertEx.Contains(generic, "A node setting is out of range");
                AssertEx.False(generic.Contains("It reported", StringComparison.Ordinal), "No report means no empty quote.");
            }

            DesktopText.Apply("de");
            AssertEx.Contains(EngineExitNotice.For(13, Tail, "/opt/xe"), "Sie meldete: DevelopmentWorkspaceRetentionOptions");
            AssertEx.Contains(EngineExitNotice.For(13, errorTail: null, "/opt/xe"), "Korrigieren Sie die Einstellung");
        }
        finally
        {
            DesktopText.Apply(previous);
        }
    }

    [Test]
    public void DescribeStartupFailure_OffersWebView2OnlyForAWindowThatCouldNotOpen()
    {
        var previous = DesktopText.Language;
        try
        {
            DesktopText.Apply("en");
            var missing = DesktopApplication.DescribeStartupFailure(new PlatformNotSupportedException("WebView2"), engineReady: true, windows: true, linuxWindow: false);
            AssertEx.Equal(DesktopText.WindowFailed, missing.Text);
            AssertEx.True(missing.IncludeWebViewLink);

            var pageLoad = DesktopApplication.DescribeStartupFailure(new DesktopPageLoadException("no page"), engineReady: true, windows: true, linuxWindow: false);
            AssertEx.Equal(DesktopText.PageLoadFailed, pageLoad.Text);
            AssertEx.False(pageLoad.IncludeWebViewLink, "A page that never loaded is the engine's side, not a missing runtime.");
            AssertEx.False(pageLoad.Text.Contains("WebView2", StringComparison.Ordinal));
            AssertEx.Equal(DesktopText.PageLoadFailed,
                DesktopApplication.DescribeStartupFailure(new DesktopPageLoadException("no page"), engineReady: true, windows: false, linuxWindow: true).Text,
                "The GTK prerequisite text is wrong for a page load too.");
            var timedOut = DesktopApplication.DescribeStartupFailure(new TimeoutException("ready deadline"), engineReady: true, windows: true, linuxWindow: false);
            AssertEx.Equal(DesktopText.PageLoadFailed, timedOut.Text, "The readiness deadline is a page that never loaded.");
            AssertEx.False(timedOut.IncludeWebViewLink, "A timeout is never a missing WebView2 runtime.");
            AssertEx.Equal(DesktopText.StartupFailed,
                DesktopApplication.DescribeStartupFailure(new TimeoutException("engine"), engineReady: false, windows: true, linuxWindow: false).Text,
                "Before the engine is ready there is no page to blame.");

            var beforeReady = DesktopApplication.DescribeStartupFailure(new InvalidOperationException("engine"), engineReady: false, windows: true, linuxWindow: false);
            AssertEx.Equal(DesktopText.StartupFailed, beforeReady.Text);
            AssertEx.False(beforeReady.IncludeWebViewLink);
            var afterReady = DesktopApplication.DescribeStartupFailure(new InvalidOperationException("window"), engineReady: true, windows: true, linuxWindow: false);
            AssertEx.Equal(DesktopText.WindowFailed, afterReady.Text);
            AssertEx.True(afterReady.IncludeWebViewLink);
        }
        finally
        {
            DesktopText.Apply(previous);
        }
    }

    [Test]
    public void EngineProbePath_IsTheEnginesAuthStatusRoute() =>
        AssertEx.Equal($"/{LocalApiRoutes.Prefix}/{LocalApiRoutes.Auth.Status}", DesktopWindow.EngineProbePath,
            "The shell's duplicate must stay the engine's route.");

    [Test]
    [Arguments("http://127.0.0.1:5000/node-settings?tab=models#top", "http://127.0.0.1:5000/node-settings?tab=models#top")]
    [Arguments("http://127.0.0.1:5000/vault?redirect=%2Fnode-settings", "http://127.0.0.1:5000/vault?redirect=%2Fnode-settings")]
    [Arguments("http://127.0.0.1:5001/node-settings", "http://127.0.0.1:5000/")]
    [Arguments("https://example.com/node-settings", "http://127.0.0.1:5000/")]
    [Arguments("about:blank", "http://127.0.0.1:5000/")]
    [Arguments("chrome-error://chromewebdata/", "http://127.0.0.1:5000/")]
    [Arguments("http://user@127.0.0.1:5000/node-settings", "http://127.0.0.1:5000/")]
    [Arguments(null, "http://127.0.0.1:5000/")]
    public void ReloadTarget_KeepsASameOriginRouteAndFallsBackToTheStartPage(string? current, string expected) =>
        AssertEx.Equal(expected, DesktopWindow.ReloadTarget(current is null ? null : new Uri(current), new Uri("http://127.0.0.1:5000/")).AbsoluteUri);

    [Test]
    public void NavigationGate_IgnoresASupersededLoadAndRetriesUntilTheWindowEnds()
    {
        var clock = new ManualClock();
        var superseded = new DesktopNavigationGate(clock);
        superseded.Started();
        superseded.Started();
        AssertEx.Equal(DesktopNavigationGate.Outcome.Superseded, superseded.Completed(success: false),
            "A failure with a newer navigation still in flight is the replaced load, not a broken page.");
        AssertEx.Equal(DesktopNavigationGate.Outcome.Loaded, superseded.Completed(success: true));

        var failing = new DesktopNavigationGate(clock);
        failing.Started();
        AssertEx.Equal(DesktopNavigationGate.Outcome.Retry, failing.Completed(success: false));
        clock.Advance(DesktopNavigationGate.RetryWindow - TimeSpan.FromMilliseconds(1));
        AssertEx.True(failing.CanRetry());
        failing.Started();
        AssertEx.Equal(DesktopNavigationGate.Outcome.Retry, failing.Completed(success: false),
            "Inside the window the engine may still be rebinding its port.");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        AssertEx.False(failing.CanRetry());
        failing.Started();
        AssertEx.Equal(DesktopNavigationGate.Outcome.Failed, failing.Completed(success: false),
            "Once the window has passed the failure is real.");

        failing.Restart();
        failing.Started();
        AssertEx.Equal(DesktopNavigationGate.Outcome.Retry, failing.Completed(success: false),
            "A reload after an engine relaunch gets a fresh window.");

        var recovered = new DesktopNavigationGate(clock);
        recovered.Started();
        AssertEx.Equal(DesktopNavigationGate.Outcome.Retry, recovered.Completed(success: false));
        clock.Advance(DesktopNavigationGate.RetryWindow);
        recovered.Started();
        AssertEx.Equal(DesktopNavigationGate.Outcome.Loaded, recovered.Completed(success: true));
        recovered.Started();
        AssertEx.Equal(DesktopNavigationGate.Outcome.Retry, recovered.Completed(success: false),
            "A load clears the failure, so a later start-page failure is timed from scratch.");

        var unannounced = new DesktopNavigationGate(clock);
        AssertEx.Equal(DesktopNavigationGate.Outcome.Retry, unannounced.Completed(success: false),
            "A completion without a reported start never drives the count below zero.");
    }

    /// <summary>A clock the test moves by hand, so the retry window is crossed without waiting.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() =>
            _now;

        internal void Advance(TimeSpan by) =>
            _now += by;
    }
}
