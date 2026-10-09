namespace XE_Local_AI_Engine.Tests.Transcription;

using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Client.Services.Transcription.Capture;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     Per-application capture ends when its target process exits: the race in
///     <c>ProcessAudioCaptureSupport.RunUntilProcessExitsAsync</c> on every host, and the WASAPI source on Windows.
/// </summary>
/// <remarks>Integration, not Unit: each case starts a real short-lived process that lives until its stdin closes.</remarks>
[Category(TestCategories.Integration)]
public sealed class ProcessAudioCaptureExitRaceTests
{
    private static readonly TimeSpan ExitBound = TimeSpan.FromSeconds(10);

    [Test]
    public async Task ExitRace_TargetExit_EndsTheCaptureAndReportsIt()
    {
        // WASAPI yields nothing once its target is gone and never ends its enumerator, so the exit watch is the only
        // thing that lets a capture of a closed application end its session.
        using var target = StartStdinBoundProcess();
        var captureToken = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);

        var race = ProcessAudioCaptureSupport.RunUntilProcessExitsAsync(target.Id, token =>
        {
            captureToken.SetResult(token);
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, NullLogger.Instance, CancellationToken.None);
        var token = await captureToken.Task.WaitAsync(ExitBound);
        AssertEx.False(race.IsCompleted, "The capture runs while the target is alive.");

        target.StandardInput.Close();

        AssertEx.True(await race.WaitAsync(ExitBound), "The target's exit ends the capture and is reported as an exit.");
        AssertEx.True(token.IsCancellationRequested, "The capture's own token is cancelled, so the recorder stops.");
    }

    [Test]
    public async Task ExitRace_CaptureThatThrowsWhileStoppingAfterTheExit_StillEndsAsAnExit()
    {
        // A recorder torn down after its target exited may fail on the way out (a device that vanished with the
        // application); the session must end normally, not as Failed.
        using var target = StartStdinBoundProcess();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var race = ProcessAudioCaptureSupport.RunUntilProcessExitsAsync(target.Id, async token =>
        {
            entered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("The audio device went away with the application.");
            }
        }, NullLogger.Instance, CancellationToken.None);
        await entered.Task.WaitAsync(ExitBound);

        target.StandardInput.Close();

        AssertEx.True(await race.WaitAsync(ExitBound), "The exit is reported and the recorder's teardown failure is swallowed.");
    }

    [Test]
    public async Task ExitRace_CallerCancel_StopsWithoutReportingAnExit()
    {
        // The control: a stop the coordinator asked for must stay a cancellation, never look like the target exited.
        using var target = StartStdinBoundProcess();
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var race = ProcessAudioCaptureSupport.RunUntilProcessExitsAsync(target.Id, token =>
        {
            entered.SetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, NullLogger.Instance, stop.Token);
        await entered.Task.WaitAsync(ExitBound);
        await stop.CancelAsync();

        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(async () => await race.WaitAsync(ExitBound),
            "The caller's cancellation propagates as one.");
        AssertEx.False(target.HasExited, "The target is untouched.");
        target.StandardInput.Close();
    }

    [Test]
    public async Task ExitRace_CaptureThatEndsOnItsOwn_IsNotAnExit()
    {
        using var target = StartStdinBoundProcess();

        var exited = await ProcessAudioCaptureSupport.RunUntilProcessExitsAsync(target.Id, static _ => Task.CompletedTask, NullLogger.Instance, CancellationToken.None)
                                                     .WaitAsync(ExitBound);

        AssertEx.False(exited, "The capture ended first; the target is still running.");
        target.StandardInput.Close();
    }

    [Test]
    public async Task ExitRace_TargetAlreadyGone_NeverStartsTheCapture()
    {
        using var target = StartStdinBoundProcess();
        var processId = target.Id;
        target.StandardInput.Close();
        await target.WaitForExitAsync().WaitAsync(ExitBound);
        var started = false;

        var exited = await ProcessAudioCaptureSupport.RunUntilProcessExitsAsync(processId, _ =>
        {
            started = true;
            return Task.CompletedTask;
        }, NullLogger.Instance, CancellationToken.None).WaitAsync(ExitBound);

        AssertEx.True(exited, "A target that is already gone counts as exited.");
        AssertEx.False(started, "No recorder is built for a process that no longer exists.");
    }

    /// <summary>A real process that lives exactly until the test closes its standard input.</summary>
    private static Process StartStdinBoundProcess()
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c more")
            : new ProcessStartInfo("cat");
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.UseShellExecute = false;
        return Process.Start(start) ?? throw new InvalidOperationException($"Could not start {start.FileName}.");
    }

    [Test]
    [RunOn(OS.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task WindowsSource_ReturnsWhenTheTargetProcessExits()
    {
        AssertEx.True(OperatingSystem.IsWindows(), "[RunOn(OS.Windows)] did not engage.");
        var source = new WindowsProcessAudioCaptureSource(Substitute.For<ILiveTranscriptionSessionRegistry>(),
            NullLogger<WindowsProcessAudioCaptureSource>.Instance);
        if (!source.IsSupported)
        {
            Skip.Test($"Process loopback needs Windows build {ProcessAudioCaptureSupport.MinimumWindowsBuild} or later.");
        }

        // A real target with no audio session: WASAPI yields nothing for it and never ends its enumerator, so only the
        // exit race can end this capture. Closing stdin is the target's exit, not a timer.
        using var target = StartStdinBoundProcess();

        var capture = source.CaptureAsync(Guid.NewGuid(), target.Id, CancellationToken.None);
        AssertEx.False(capture.IsCompleted, "The capture runs while the target is alive.");
        target.StandardInput.Close();

        await capture.WaitAsync(ExitBound);
    }
}
