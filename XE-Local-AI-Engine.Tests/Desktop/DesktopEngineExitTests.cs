namespace XE_Local_AI_Engine.Tests.Desktop;

using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Desktop;
using XE_Local_AI_Engine.Tests.Testing;

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
        AssertEx.Equal(XE_Local_AI_Engine.Client.Program.ApplicationControlBlockedExitCode, DesktopApplication.ApplicationControlBlockedExitCode,
            "The shell's duplicate must stay the engine's code.");
        AssertEx.Equal(ApplicationControlBlock.StandardErrorPrefix, EngineExitNotice.BlockedPrefix,
            "The shell's prefix must stay the engine's.");
        AssertEx.Equal(11, DesktopApplication.StartupFailureExitCode(new DesktopEngineStartupException("engine", 11), engineReady: false));
    }

    [Test]
    [Arguments("Quartz.Serialization.SystemTextJson.dll")]
    [Arguments("Quartz.Serialization.SystemTextJson, Version=3.22.6.0, Culture=neutral, PublicKeyToken=null")]
    [Arguments("an engine file")]
    public void BlockedFile_ReadsTheFileFromTheEnginesOwnReport(string file)
    {
        AssertEx.Equal(file, EngineExitNotice.BlockedFile(ApplicationControlBlock.Describe(file) + "\r\nnext line"));
        AssertEx.Equal(file, EngineExitNotice.BlockedFile(
                "The engine exited before readiness (exit code 11). It reported: " + ApplicationControlBlock.Describe(file)),
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
    public void NavigationGate_IgnoresASupersededLoadAndRetriesOnceBeforeFailing()
    {
        var superseded = new DesktopNavigationGate();
        superseded.Started();
        superseded.Started();
        AssertEx.Equal(DesktopNavigationGate.Outcome.Superseded, superseded.Completed(success: false),
            "A failure with a newer navigation still in flight is the replaced load, not a broken page.");
        AssertEx.Equal(DesktopNavigationGate.Outcome.Loaded, superseded.Completed(success: true));

        var failing = new DesktopNavigationGate();
        failing.Started();
        AssertEx.Equal(DesktopNavigationGate.Outcome.Retry, failing.Completed(success: false));
        failing.Started();
        AssertEx.Equal(DesktopNavigationGate.Outcome.Failed, failing.Completed(success: false),
            "The start page is retried once, then the failure is real.");

        var recovered = new DesktopNavigationGate();
        recovered.Started();
        AssertEx.Equal(DesktopNavigationGate.Outcome.Retry, recovered.Completed(success: false));
        recovered.Started();
        AssertEx.Equal(DesktopNavigationGate.Outcome.Loaded, recovered.Completed(success: true));

        var unannounced = new DesktopNavigationGate();
        AssertEx.Equal(DesktopNavigationGate.Outcome.Retry, unannounced.Completed(success: false),
            "A completion without a reported start never drives the count below zero.");
    }
}
