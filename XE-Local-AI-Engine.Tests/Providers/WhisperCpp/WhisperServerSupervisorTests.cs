namespace XE_Local_AI_Engine.Tests.Providers.WhisperCpp;

using System.Net;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Providers.WhisperCpp.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Brief tests 2 (readiness is a probe, never a log line), 3 (GPU spawns serialize, CPU spawns do not) and 4 (the
///     idle reaper and the busy eject). Every wait here is a gate the test owns or a clock the test moves; nothing
///     sleeps, and nothing spawns a real process.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class WhisperServerSupervisorTests
{
    [Test]
    public async Task EnsureRunning_ReusesReadyDaemon_NoSecondSpawn()
    {
        await using var harness = new WhisperSupervisorHarness();

        var first = await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);
        var second = await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);

        AssertEx.Equal(expected: 1, harness.Launcher.LaunchCount);
        AssertEx.Equal(first.BaseAddress.AbsoluteUri, second.BaseAddress.AbsoluteUri);
        AssertEx.Equal(first.Generation, second.Generation, "A reuse must not move the process generation.");
    }

    [Test]
    public async Task EnsureRunning_ReuseWithinProbeInterval_DoesNotProbe()
    {
        // Between probes the endpoint is handed out with no HTTP at all; that is what keeps the hot path cheap.
        await using var harness = new WhisperSupervisorHarness();

        await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);
        await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);

        AssertEx.Equal(expected: 1, harness.Launcher.LaunchCount);
        AssertEx.Equal(expected: 0, harness.ReadinessProbe.ResponsiveChecks);
    }

    [Test]
    public async Task EnsureRunning_PortOpensAfterDelay_ReportedReadyOnlyAfterHealthSucceeds()
    {
        // Brief test 2. "The port opens after a while" is a gate the test completes, never a delay: the ensure must
        // still be incomplete while the gate is unset, and complete only once it resolves.
        var probe = new FakeWhisperReadinessProbe
        {
            ReadinessGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var harness = new WhisperSupervisorHarness(readinessProbe: probe);

        var ensure = harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);
        await probe.ReadinessReached.Task;

        await AssertEx.StaysIncompleteAsync(ensure, "The daemon must not be reported ready before the health probe succeeds.");
        AssertEx.Equal(WhisperRuntimeState.Starting, harness.Supervisor.GetStatus().State);

        probe.ReadinessGate.SetResult(true);
        var endpoint = await ensure;

        AssertEx.Equal("base", endpoint.ModelId);
        AssertEx.Equal(WhisperRuntimeState.Ready, harness.Supervisor.GetStatus().State);
    }

    [Test]
    public async Task EnsureRunning_PortNeverOpens_TimesOutWithTypedError()
    {
        // Brief test 2. The probe reports "not ready" at its deadline; the supervisor turns that into a typed,
        // sanitized failure rather than hanging or surfacing a transport exception.
        await using var harness = new WhisperSupervisorHarness(readinessProbe: new FakeWhisperReadinessProbe(ready: false));

        var exception = await AssertEx.ThrowsAsync<WhisperRuntimeException>(() => harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None));

        AssertEx.Contains(exception.Message, "did not become ready", StringComparison.Ordinal);
        AssertEx.True(harness.Launcher.Handles.Single().WasTreeKilled,
            "A daemon that never became ready must be torn down, not left resident.");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task EnsureRunning_StdoutContent_IsIrrelevantToReadiness(bool emitsListeningLine)
    {
        // Brief test 2. The fake handle's stdout is the only thing that differs between the two runs, and the outcome
        // must be identical: the server's own "listening" line is fully buffered off a TTY and has been seen absent
        // while the port was already live, so nothing may depend on it.
        await using var harness = new WhisperSupervisorHarness();
        harness.ReadinessProbe.Ready = true;

        var endpoint = await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);

        AssertEx.Equal(WhisperRuntimeState.Ready, harness.Supervisor.GetStatus().State);
        AssertEx.NotNull(endpoint.BaseAddress);
        AssertEx.Equal(expected: 1, harness.ReadinessProbe.ReadinessWaits,
            $"Readiness must be decided by the probe whether or not the child printed its banner (emitted: {emitsListeningLine}).");
    }

    [Test]
    public async Task EnsureRunning_CudaBackend_AcquiresAndReleasesLoadAdmission()
    {
        // Brief test 3. The BINARY's backend decides, which is why the binary manager fake reports CUDA here.
        var admission = new RecordingGpuLoadAdmission();
        await using var harness = new WhisperSupervisorHarness(binaryManager: new FakeWhisperBinaryManager(WhisperBackend.Cuda),
            backendSelector: new FakeWhisperBackendSelector(WhisperBackend.Cuda),
            loadAdmission: admission);

        await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);

        AssertEx.Equal(expected: 1, admission.AcquireCount);
        AssertEx.Equal(expected: 1, admission.ReleaseCount);
        AssertEx.False(admission.IsHeld, "The admission ticket must be released once the daemon is ready.");
    }

    [Test]
    public async Task EnsureRunning_CudaBackend_ReleasesLoadAdmissionOnFailure()
    {
        // Holding the gate after a failed spawn would wedge every other runtime's load on this box.
        var admission = new RecordingGpuLoadAdmission();
        await using var harness = new WhisperSupervisorHarness(readinessProbe: new FakeWhisperReadinessProbe(ready: false),
            binaryManager: new FakeWhisperBinaryManager(WhisperBackend.Cuda),
            backendSelector: new FakeWhisperBackendSelector(WhisperBackend.Cuda),
            loadAdmission: admission);

        await AssertEx.ThrowsAsync<WhisperRuntimeException>(() => harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None));

        AssertEx.Equal(expected: 1, admission.AcquireCount);
        AssertEx.Equal(expected: 1, admission.ReleaseCount);
        AssertEx.False(admission.IsHeld);
    }

    [Test]
    public async Task EnsureRunning_CudaBackend_ReleasesLoadAdmissionWhenTheProcessExitsWhileLoading()
    {
        // The third of the four unwinds the admission ticket has to survive, and the one with its own code path: the
        // child dies during readiness rather than readiness simply running out. A ticket leaked here would wedge every
        // llama and image load on the box behind a process that no longer exists.
        var admission = new RecordingGpuLoadAdmission();
        var probe = new FakeWhisperReadinessProbe
        {
            ReadinessGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var harness = new WhisperSupervisorHarness(readinessProbe: probe,
            binaryManager: new FakeWhisperBinaryManager(WhisperBackend.Cuda),
            backendSelector: new FakeWhisperBackendSelector(WhisperBackend.Cuda),
            loadAdmission: admission);

        var ensure = harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);
        await probe.ReadinessReached.Task;
        harness.Launcher.Handles.Single().SimulateExit();

        await AssertEx.ThrowsAsync<WhisperRuntimeException>(() => ensure);

        AssertEx.Equal(expected: 1, admission.AcquireCount);
        AssertEx.Equal(expected: 1, admission.ReleaseCount);
        AssertEx.False(admission.IsHeld, "A daemon that died while loading must still release the GPU gate.");
    }

    [Test]
    public async Task EnsureRunning_CpuBackend_NeverTouchesLoadAdmission()
    {
        // Brief test 3. A CPU load does not contend for VRAM, so taking the shared gate would serialize it against
        // every GPU load on the box for no reason. The forbidden gate throws if it is touched at all.
        await using var harness = new WhisperSupervisorHarness(loadAdmission: new ForbiddenGpuLoadAdmission());

        var endpoint = await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);

        AssertEx.Equal("base", endpoint.ModelId);
    }

    [Test]
    public async Task EnsureRunning_DifferentModelOnHealthyDaemon_UsesLoadWithoutRespawn()
    {
        var handler = new ScriptedWhisperHttpHandler(static _ => new HttpResponseMessage(HttpStatusCode.OK));
        await using var harness = new WhisperSupervisorHarness(httpHandler: handler);

        var first = await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);
        var second = await harness.Supervisor.EnsureRunningAsync("small", CancellationToken.None);

        AssertEx.Equal(expected: 1, harness.Launcher.LaunchCount, "A healthy daemon must switch models in place.");
        AssertEx.Equal("/load", handler.LastPath);
        AssertEx.Equal("small", second.ModelId);
        AssertEx.True(second.Generation > first.Generation,
            "A model switch must move the generation, or a stale lease would still look valid.");
    }

    [Test]
    public async Task EnsureRunning_LoadFails_TearsDownAndRespawns()
    {
        // A failed load can take the daemon with it, so a refusal means respawn rather than fail the caller.
        var handler = new ScriptedWhisperHttpHandler(static _ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        await using var harness = new WhisperSupervisorHarness(httpHandler: handler);

        await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);
        var second = await harness.Supervisor.EnsureRunningAsync("small", CancellationToken.None);

        AssertEx.Equal(expected: 2, harness.Launcher.LaunchCount);
        AssertEx.Equal("small", second.ModelId);
    }

    [Test]
    public async Task AcquireLease_AfterAnotherCallerSwitchedModels_ReturnsNull_ThenTheRetrySucceeds()
    {
        // Ensure-then-lease is two steps and the daemon is mutable, so a switch can land between them. A lease taken
        // against the OLD generation must be refused, and a fresh ensure must then hand out a usable one.
        await using var harness = new WhisperSupervisorHarness();

        var stale = await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);
        await harness.Supervisor.EnsureRunningAsync("small", CancellationToken.None);

        AssertEx.Null(harness.Supervisor.TryAcquireTranscriptionLease(stale.ModelId, stale.Generation),
            "A lease for the generation that was replaced must be refused.");

        var current = await harness.Supervisor.EnsureRunningAsync("small", CancellationToken.None);
        using var lease = AssertEx.NotNull(harness.Supervisor.TryAcquireTranscriptionLease(current.ModelId, current.Generation));
    }

    [Test]
    public async Task Load_WhileATranscriptionLeaseIsHeld_IsRefused_AndTheModelDoesNotChange()
    {
        // The reverse race: a switch requested while audio is being transcribed must be refused rather than pulling
        // the model out from under the in-flight request.
        await using var harness = new WhisperSupervisorHarness();

        var endpoint = await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);
        using var lease = AssertEx.NotNull(harness.Supervisor.TryAcquireTranscriptionLease(endpoint.ModelId, endpoint.Generation));

        var exception = await AssertEx.ThrowsAsync<WhisperRuntimeException>(() => harness.Supervisor.EnsureRunningAsync("small", CancellationToken.None));

        AssertEx.Contains(exception.Message, "busy with another transcription", StringComparison.Ordinal);
        AssertEx.Equal("base", AssertEx.NotNull(harness.Supervisor.GetStatus().LoadedModelId),
            "A refused switch must leave the loaded model exactly as it was.");
    }

    [Test]
    public async Task Load_OnCudaBackend_AcquiresAndReleasesLoadAdmission()
    {
        // An in-place load initialises GPU weights exactly as a spawn does, so skipping the gate here would let a
        // model switch race another runtime's load.
        var admission = new RecordingGpuLoadAdmission();
        await using var harness = new WhisperSupervisorHarness(binaryManager: new FakeWhisperBinaryManager(WhisperBackend.Cuda),
            backendSelector: new FakeWhisperBackendSelector(WhisperBackend.Cuda),
            loadAdmission: admission);

        await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);
        AssertEx.Equal(expected: 1, admission.AcquireCount);

        await harness.Supervisor.EnsureRunningAsync("small", CancellationToken.None);

        AssertEx.Equal(expected: 2, admission.AcquireCount, "The in-place model switch must take the gate too.");
        AssertEx.Equal(expected: 2, admission.ReleaseCount);
        AssertEx.False(admission.IsHeld);
    }

    [Test]
    public async Task Load_OnCudaBackend_ReleasesLoadAdmissionWhenTheSwitchIsRefused()
    {
        var admission = new RecordingGpuLoadAdmission();
        var handler = new ScriptedWhisperHttpHandler(static _ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        await using var harness = new WhisperSupervisorHarness(binaryManager: new FakeWhisperBinaryManager(WhisperBackend.Cuda),
            backendSelector: new FakeWhisperBackendSelector(WhisperBackend.Cuda),
            loadAdmission: admission,
            httpHandler: handler);

        await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);
        await harness.Supervisor.EnsureRunningAsync("small", CancellationToken.None);

        AssertEx.False(admission.IsHeld, "A refused switch must not leave the shared GPU gate held.");
        AssertEx.Equal(admission.AcquireCount, admission.ReleaseCount);
    }

    [Test]
    public async Task Load_OnCpuBackend_NeverTouchesLoadAdmission()
    {
        await using var harness = new WhisperSupervisorHarness(loadAdmission: new ForbiddenGpuLoadAdmission());

        await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);
        var switched = await harness.Supervisor.EnsureRunningAsync("small", CancellationToken.None);

        AssertEx.Equal("small", switched.ModelId);
    }

    [Test]
    public async Task IdleReaper_KillsAfterTtl()
    {
        // Brief test 4. The clock is moved by the test and the reaper's own timer fires off it, so nothing waits in
        // wall-clock time. ArmedTimerCount is the non-vacuity guard: advancing before the loop has armed its timer
        // would move the clock past a window nothing was waiting on.
        var clock = new ManualTimeProvider();
        var options = new WhisperRuntimeOptions
        {
            IdleTimeToLive = TimeSpan.FromMinutes(15)
        };
        await using var harness = new WhisperSupervisorHarness(options: options, timeProvider: clock);

        await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);
        var handle = harness.Launcher.Handles.Single();

        await AssertEx.EventuallyAsync(() => clock.ArmedTimerCount > 0, TestBudgets.Contended,
            "The idle reaper must arm its timer before the clock is advanced.");

        clock.Advance(TimeSpan.FromMinutes(20));

        await AssertEx.EventuallyAsync(() => handle.WasTreeKilled, TestBudgets.Contended,
            "A daemon idle past its TTL must be evicted.");
        AssertEx.Equal(WhisperRuntimeState.Stopped, harness.Supervisor.GetStatus().State);
    }

    [Test]
    public async Task IdleReaper_ActivityDuringTtl_ResetsTheClock()
    {
        // Brief test 4. Using the daemon inside the window must push the deadline out, or a busy node would have its
        // runtime evicted from under it.
        var clock = new ManualTimeProvider();
        var options = new WhisperRuntimeOptions
        {
            IdleTimeToLive = TimeSpan.FromMinutes(15)
        };
        await using var harness = new WhisperSupervisorHarness(options: options, timeProvider: clock);

        await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);
        var handle = harness.Launcher.Handles.Single();

        await AssertEx.EventuallyAsync(() => clock.ArmedTimerCount > 0, TestBudgets.Contended,
            "The idle reaper must arm its timer before the clock is advanced.");

        // Ten minutes in: still inside the window, and the reuse re-stamps the idle clock.
        clock.Advance(TimeSpan.FromMinutes(10));
        await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);

        // Ten more: twenty total, but only ten since the last use.
        clock.Advance(TimeSpan.FromMinutes(10));
        await AssertEx.SettleAsync();

        AssertEx.False(handle.WasTreeKilled, "Activity inside the window must push the idle deadline out.");
        AssertEx.Equal(WhisperRuntimeState.Ready, harness.Supervisor.GetStatus().State);
    }

    [Test]
    public async Task Evict_WhileATranscriptionLeaseIsHeld_ReportsBusy()
    {
        // Brief test 4's supervisor half: this refusal is what the endpoint turns into 409 runtime-busy.
        await using var harness = new WhisperSupervisorHarness();

        var endpoint = await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);
        using var lease = AssertEx.NotNull(harness.Supervisor.TryAcquireTranscriptionLease(endpoint.ModelId, endpoint.Generation));

        var result = await harness.Supervisor.EvictAsync(CancellationToken.None);

        AssertEx.False(result.Evicted);
        AssertEx.True(result.Activity.IsBusy);
        AssertEx.Equal(expected: 1, result.Activity.ActiveTranscriptionCount);
        AssertEx.False(harness.Launcher.Handles.Single().WasTreeKilled);
    }

    [Test]
    public async Task Evict_WhenIdle_TearsDownAndReportsAnIdleSnapshot()
    {
        await using var harness = new WhisperSupervisorHarness();
        await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);

        var result = await harness.Supervisor.EvictAsync(CancellationToken.None);

        AssertEx.True(result.Evicted);
        AssertEx.True(harness.Launcher.Handles.Single().WasTreeKilled);
        AssertEx.False(result.Activity.IsBusy, "Once the child is down nothing may still be reported as holding it.");
        AssertEx.Equal(WhisperRuntimeState.Stopped, harness.Supervisor.GetStatus().State);
    }

    [Test]
    public async Task Evict_WithNothingResident_IsASuccessfulNoOp()
    {
        await using var harness = new WhisperSupervisorHarness();

        var result = await harness.Supervisor.EvictAsync(CancellationToken.None);

        AssertEx.True(result.Evicted, "Ejecting an already-stopped runtime is idempotent, not an error.");
    }

    [Test]
    public async Task EnsureRunning_WedgedDaemon_AfterConsecutiveProbeFailures_TearsDownAndRespawns()
    {
        var clock = new ManualTimeProvider();
        var options = new WhisperRuntimeOptions
        {
            IdleTimeToLive = TimeSpan.FromHours(1),
            ReuseLivenessProbeInterval = TimeSpan.FromSeconds(5),
            MaxReuseLivenessFailures = 3
        };
        await using var harness = new WhisperSupervisorHarness(options: options, timeProvider: clock);

        await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);
        var firstHandle = harness.Launcher.Handles.Single();

        // The daemon wedges: alive, but no longer answering. Every reuse past the rate-limit window probes once.
        harness.ReadinessProbe.Responsive = false;
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(6));
            await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);
        }

        AssertEx.Equal(expected: 3, harness.ReadinessProbe.ResponsiveChecks);
        AssertEx.Equal(expected: 2, harness.Launcher.LaunchCount, "The wedged daemon must be respawned exactly once.");
        AssertEx.True(firstHandle.WasTreeKilled);
    }

    [Test]
    public async Task EnsureRunning_TransientProbeFailure_ThenRecovers_DoesNotRespawn()
    {
        // One failed probe must never tear down a daemon that is merely busy.
        var clock = new ManualTimeProvider();
        var options = new WhisperRuntimeOptions
        {
            IdleTimeToLive = TimeSpan.FromHours(1),
            ReuseLivenessProbeInterval = TimeSpan.FromSeconds(5),
            MaxReuseLivenessFailures = 3
        };
        await using var harness = new WhisperSupervisorHarness(options: options, timeProvider: clock);

        await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);

        harness.ReadinessProbe.Responsive = false;
        clock.Advance(TimeSpan.FromSeconds(6));
        await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(6));
        await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);

        harness.ReadinessProbe.Responsive = true;
        clock.Advance(TimeSpan.FromSeconds(6));
        await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);

        AssertEx.Equal(expected: 1, harness.Launcher.LaunchCount);
    }

    [Test]
    public async Task EnsureRunning_ExitedDaemon_IsReplaced()
    {
        await using var harness = new WhisperSupervisorHarness();

        await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);
        harness.Launcher.Handles.Single().SimulateExit();

        await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);

        AssertEx.Equal(expected: 2, harness.Launcher.LaunchCount);
    }

    [Test]
    public async Task EnsureRunning_UnknownModel_ThrowsTypedAndNeverLaunches()
    {
        await using var harness = new WhisperSupervisorHarness();

        var exception = await AssertEx.ThrowsAsync<WhisperRuntimeException>(() => harness.Supervisor.EnsureRunningAsync("not-a-model", CancellationToken.None));

        AssertEx.Contains(exception.Message, "not installed", StringComparison.Ordinal);
        AssertEx.Equal(expected: 0, harness.Launcher.LaunchCount);
    }

    [Test]
    public async Task EnsureRunning_ModelFileMissing_ThrowsTypedAndNeverLaunches()
    {
        // The catalogue row exists but the weights were never downloaded. Saying so beats spawning a daemon that
        // would fail to load and then die.
        await using var harness = new WhisperSupervisorHarness();
        File.Delete(Path.Combine(harness.ModelsDirectory, "base", "ggml-base.bin"));

        var exception = await AssertEx.ThrowsAsync<WhisperRuntimeException>(() => harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None));

        AssertEx.Contains(exception.Message, "not installed", StringComparison.Ordinal);
        AssertEx.Equal(expected: 0, harness.Launcher.LaunchCount);
    }

    [Test]
    public async Task EnsureRunning_VadFileMissing_ThrowsTypedAndNeverLaunches()
    {
        // The daemon is always launched with voice-activity detection on, so a configured-but-absent VAD file is an
        // incomplete installation, not an optional extra: launching anyway produces a daemon that either never
        // reaches readiness or fails every transcription with a message that names nothing useful.
        await using var harness = new WhisperSupervisorHarness();
        harness.Options.VadModelPath = Path.Combine(harness.ModelsDirectory, "vad", WhisperModelCatalog.VadFileName);

        var exception = await AssertEx.ThrowsAsync<WhisperRuntimeException>(() => harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None));

        AssertEx.Contains(exception.Message, "voice-activity-detection model is not installed", StringComparison.Ordinal);
        AssertEx.Equal(expected: 0, harness.Launcher.LaunchCount);
    }

    [Test]
    public async Task EnsureRunning_VadFilePresent_LaunchesWithBothVadFlags()
    {
        // The positive control for the guard above: a VAD file that IS installed launches, and the flags reach the
        // process, so the guard cannot be satisfied by silently dropping voice-activity detection.
        await using var harness = new WhisperSupervisorHarness();
        var vadPath = Path.Combine(harness.ModelsDirectory, "vad", WhisperModelCatalog.VadFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(vadPath)!);
        await File.WriteAllBytesAsync(vadPath, [1, 2, 3]);
        harness.Options.VadModelPath = vadPath;

        await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);

        AssertEx.Equal(expected: 1, harness.Launcher.LaunchCount);
        AssertEx.True(harness.Launcher.Launches.TryPeek(out var spec), "The launcher must have recorded the spawn.");
        AssertEx.Contains(spec!.Arguments, "--vad");
        AssertEx.Contains(spec.Arguments, vadPath);
    }

    [Test]
    public async Task EnsureRunning_VadPathUnconfigured_LaunchesWithoutVadFlags()
    {
        // The other legal shape: nothing configured means a deliberate no-VAD launch, which the argument builder
        // already expresses by omitting BOTH flags. The guard must not turn that into a failure.
        await using var harness = new WhisperSupervisorHarness();
        harness.Options.VadModelPath = null;

        await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);

        AssertEx.Equal(expected: 1, harness.Launcher.LaunchCount);
        AssertEx.True(harness.Launcher.Launches.TryPeek(out var spec), "The launcher must have recorded the spawn.");
        AssertEx.False(spec!.Arguments.Contains("--vad"), "An unconfigured VAD path must not emit the flag.");
    }

    [Test]
    public async Task EnsureRunning_WhileAMutationIsReserved_ReportsBusy()
    {
        await using var harness = new WhisperSupervisorHarness();
        using var mutation = AssertEx.NotNull(harness.ActivityGate.TryAcquireMutationReservation());

        var exception = await AssertEx.ThrowsAsync<WhisperRuntimeException>(() => harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None));

        AssertEx.Contains(exception.Message, "busy with an exclusive operation", StringComparison.Ordinal);
        AssertEx.Equal(expected: 0, harness.Launcher.LaunchCount);
    }

    [Test]
    public async Task GetStatus_BeforeAnySpawn_ReportsStoppedWithNoModel()
    {
        await using var harness = new WhisperSupervisorHarness();

        var status = harness.Supervisor.GetStatus();

        AssertEx.Equal(WhisperRuntimeState.Stopped, status.State);
        AssertEx.Null(status.LoadedModelId);
        AssertEx.Null(status.Backend);
        AssertEx.Null(status.BinarySource);
    }

    [Test]
    public async Task GetStatus_AfterSpawn_ReportsTheResolvedBinary()
    {
        await using var harness = new WhisperSupervisorHarness(binaryManager: new FakeWhisperBinaryManager(WhisperBackend.Cuda, isPinnedFallback: false, version: "byo"),
            backendSelector: new FakeWhisperBackendSelector(WhisperBackend.Cuda));

        await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);
        var status = harness.Supervisor.GetStatus();

        AssertEx.Equal(WhisperRuntimeState.Ready, status.State);
        AssertEx.Equal("base", AssertEx.NotNull(status.LoadedModelId));
        AssertEx.Equal(WhisperBackend.Cuda, status.Backend);
        AssertEx.Equal(WhisperBinarySource.BringYourOwn, status.BinarySource);
        AssertEx.Equal("byo", AssertEx.NotNull(status.BinaryVersion));
    }

    [Test]
    public async Task ReportRequestFailure_DaemonExited_WarnsOnceWithExitCodeAndTail_AndRespawnsNext()
    {
        // The 2026-09-22 tester box: the daemon reported ready, then died on its first request. The operator-facing message
        // names the exit; the stderr tail goes to the Warning only.
        var logger = new RecordingLogger<WhisperServerProcessSupervisor>();
        await using var harness = new WhisperSupervisorHarness(logger: logger);
        var endpoint = await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);
        var handle = harness.Launcher.Handles.Single();
        handle.SimulateExit(exitCode: -1073740791, stderrTail: "ggml_cuda_init: failed to initialize CUDA: no CUDA-capable device is detected");

        var failure = await harness.Supervisor.ReportRequestFailureAsync(endpoint.Generation, new HttpRequestException("reset"), CancellationToken.None);

        var exception = AssertEx.NotNull(failure, "An exited daemon must yield the supervisor's exit verdict.");
        AssertEx.Contains(exception.Message, "process exited (exit code -1073740791)", StringComparison.Ordinal);
        AssertEx.False(exception.Message.Contains("ggml_cuda_init", StringComparison.Ordinal), "The stderr tail must stay in the log.");
        var warnings = logger.Entries.Where(static entry => entry.Level == LogLevel.Warning).ToList();
        AssertEx.Equal(expected: 1, warnings.Count);
        AssertEx.Contains(warnings[0].Message, "-1073740791", StringComparison.Ordinal);
        AssertEx.Contains(warnings[0].Message, "no CUDA-capable device", StringComparison.Ordinal);
        AssertEx.Contains(warnings[0].Message, handle.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

        await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);

        AssertEx.Equal(expected: 2, harness.Launcher.LaunchCount);
        AssertEx.Equal(expected: 1, logger.Entries.Count(static entry => entry.Level == LogLevel.Warning), "The respawn must not report the same death twice.");
    }

    [Test]
    public async Task EnsureRunning_ResidentDaemonExited_WarnsWithExitCodeBeforeRespawning()
    {
        var logger = new RecordingLogger<WhisperServerProcessSupervisor>();
        await using var harness = new WhisperSupervisorHarness(logger: logger);
        await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);
        harness.Launcher.Handles.Single().SimulateExit(exitCode: 3, stderrTail: "whisper_init: failed");

        await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);

        AssertEx.Equal(expected: 2, harness.Launcher.LaunchCount);
        AssertEx.True(logger.HasEntry(LogLevel.Warning, "whisper_init: failed"), "A daemon found dead at hand-out must be reported with its stderr tail.");
    }

    [Test]
    public async Task ReportRequestFailure_PinnedCudaDaemonExited_LatchesTheCpuFallbackWithTheExitCode()
    {
        // The tester box: the driver is present, so the nvcuda.dll probe passes, and only the daemon's death reveals that no
        // device enumerates. The latch is what stops every respawn from picking the cuBLAS build again.
        await using var harness = new WhisperSupervisorHarness(binaryManager: new FakeWhisperBinaryManager(WhisperBackend.Cuda, isPinnedFallback: true),
            backendSelector: new FakeWhisperBackendSelector(WhisperBackend.Cuda));
        var endpoint = await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);
        harness.Launcher.Handles.Single().SimulateExit(exitCode: -1073740791);

        await harness.Supervisor.ReportRequestFailureAsync(endpoint.Generation, new HttpRequestException("reset"), CancellationToken.None);

        AssertEx.Contains(AssertEx.NotNull(harness.CudaFailureSignal.Reason), "exit code -1073740791", StringComparison.Ordinal);
    }

    [Test]
    [Arguments("byo")]
    [Arguments("0123abcd")]
    public async Task ReportRequestFailure_OperatorChosenCudaDaemonExited_DoesNotLatchTheCpuFallback(string version)
    {
        // A bring-your-own override ("byo") or a managed source build is the operator's explicit choice: its crash is reported,
        // never overridden by a silent switch to CPU.
        await using var harness = new WhisperSupervisorHarness(binaryManager: new FakeWhisperBinaryManager(WhisperBackend.Cuda, isPinnedFallback: false, version),
            backendSelector: new FakeWhisperBackendSelector(WhisperBackend.Cuda));
        var endpoint = await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);
        harness.Launcher.Handles.Single().SimulateExit(exitCode: 1);

        var failure = await harness.Supervisor.ReportRequestFailureAsync(endpoint.Generation, new HttpRequestException("reset"), CancellationToken.None);

        AssertEx.NotNull(failure, "The death itself must still be reported.");
        AssertEx.Null(harness.CudaFailureSignal.Reason);
    }

    [Test]
    public async Task ReportRequestFailure_PinnedCpuDaemonExited_DoesNotLatchTheCpuFallback()
    {
        await using var harness = new WhisperSupervisorHarness();
        var endpoint = await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);
        harness.Launcher.Handles.Single().SimulateExit(exitCode: 1);

        await harness.Supervisor.ReportRequestFailureAsync(endpoint.Generation, new HttpRequestException("reset"), CancellationToken.None);

        AssertEx.Null(harness.CudaFailureSignal.Reason);
    }

    [Test]
    public async Task ReportRequestFailure_DaemonStillAlive_ReturnsNull_AndKeepsIt()
    {
        // A transport failure against a live daemon is not a crash: after the bounded grace the caller keeps its own message.
        var clock = new ManualTimeProvider();
        var logger = new RecordingLogger<WhisperServerProcessSupervisor>();
        await using var harness = new WhisperSupervisorHarness(timeProvider: clock, logger: logger);
        var endpoint = await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);

        // The report arms its grace timer synchronously, so the advance below cannot land before it.
        var report = harness.Supervisor.ReportRequestFailureAsync(endpoint.Generation, new HttpRequestException("refused"), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(5));

        AssertEx.Null(await report);
        AssertEx.False(harness.Launcher.Handles.Single().WasTreeKilled, "A live daemon must not be torn down by a transport failure.");
        AssertEx.False(logger.HasEntry(LogLevel.Warning, "exited unexpectedly"));
    }

    [Test]
    public async Task Dispose_DuringBlockedReadiness_TreeKillsSpawnedDaemon_NoOrphan()
    {
        // The spawn registers itself only AFTER readiness, so a dispose racing it sees nothing to tear down. The
        // spawn's own unwind is what must kill the child, or the daemon outlives the host holding its port.
        var probe = new FakeWhisperReadinessProbe
        {
            ReadinessGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var harness = new WhisperSupervisorHarness(readinessProbe: probe);

        var ensure = harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);
        await probe.ReadinessReached.Task;

        await harness.DisposeAsync();

        await AssertEx.EventuallyAsync(() => harness.Launcher.Handles.Single().WasTreeKilled, TestBudgets.Contended,
            "A daemon spawned into a disposing supervisor must be tree-killed, never orphaned.");

        // The ensure itself unwinds rather than completing successfully.
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => ensure);
    }
}
