namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies that a repeatedly-failing spawn retries up to the restart cap with backoff and then surfaces a
///     sanitized <see cref="LlamaRuntimeException" /> (no internal paths/secrets); a not-installed model surfaces the
///     same way. Also covers the hybrid external-endpoint attach path (attach to a configured endpoint instead of
///     spawning a local process) and the per-process health aggregation surface.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class SupervisorCrashAndSurfaceTests
{
    [Test]
    public async Task EnsureRunning_KilledButExitNotYetObserved_ReusesTheDeadEndpoint_UntilWaitForProcessExitSeesTheExit()
    {
        // REGRESSION (live QA F-18): a SIGKILLed server's sockets close before the parent reaps it, so an immediate
        // re-ensure reuses the dead endpoint. WaitForProcessExitAsync is the barrier that lets the retry respawn.
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);
        var first = await supervisor.EnsureRunningAsync("model-a", ModelRole.Embedding, CancellationToken.None);
        var handle = launcher.Handles.Single();

        var racing = await supervisor.EnsureRunningAsync("model-a", ModelRole.Embedding, CancellationToken.None);
        AssertEx.Equal(first.BaseAddress, racing.BaseAddress);
        AssertEx.Equal(expected: 1, launcher.LaunchCount);

        // real-timer: an upper bound only; the wait completes on SimulateExit, the signal the test controls.
        var wait = supervisor.WaitForProcessExitAsync("model-a", ModelRole.Embedding, TimeSpan.FromSeconds(30), CancellationToken.None);
        AssertEx.False(wait.IsCompleted, "The wait must not complete before the exit is observed.");
        handle.SimulateExit();
        AssertEx.True(await wait);

        _ = await supervisor.EnsureRunningAsync("model-a", ModelRole.Embedding, CancellationToken.None);
        AssertEx.Equal(expected: 2, launcher.LaunchCount);
    }

    [Test]
    public async Task Admission_TrackedProcessExitedOutsideTheSupervisor_PrunesIt_AndLogsAWarningNamingTheModel()
    {
        // REGRESSION (tester round 3): a child killed from outside the node (another checkout's stale reaper) was pruned
        // at the next admission with no log line, so the model vanished from "loaded models" without a trace.
        var launcher = new FakeProcessLauncher();
        var logger = new RecordingLogger<LlamaServerProcessSupervisor>();
        await using var supervisor = SupervisorFactory.Create(launcher, logger: logger);
        _ = await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        var handle = launcher.Handles.Single();
        handle.SimulateExit(exitCode: 137);

        // A different model's admission runs the prune; model-a's own re-ensure would take the respawn path instead.
        _ = await supervisor.EnsureRunningAsync("model-b", ModelRole.Chat, CancellationToken.None);

        var warning = logger.Entries.Single(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("exited outside the supervisor's control", StringComparison.Ordinal));
        AssertEx.Contains(warning.Message, "model-a", StringComparison.Ordinal);
        AssertEx.Contains(warning.Message, $"pid {handle.ProcessId}", StringComparison.Ordinal);
        AssertEx.Contains(warning.Message, "exit code 137", StringComparison.Ordinal);
        AssertEx.True(handle.WasDisposed, "The pruned handle must still be torn down.");
    }

    [Test]
    public async Task EnsureRunning_DeadModelReRequested_RespawnsIt_AndLogsTheExitWarningExactlyOnce()
    {
        // The same-key respawn detaches the corpse through RemoveProcessAsync, not the admission prune, and must leave the same single trace.
        var launcher = new FakeProcessLauncher();
        var logger = new RecordingLogger<LlamaServerProcessSupervisor>();
        await using var supervisor = SupervisorFactory.Create(launcher, logger: logger);
        _ = await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        var handle = launcher.Handles.Single();
        handle.SimulateExit(exitCode: 137);

        _ = await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(expected: 2, launcher.LaunchCount);
        var warnings = logger.Entries.Where(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("exited outside the supervisor's control", StringComparison.Ordinal)).ToList();
        AssertEx.Equal(expected: 1, warnings.Count);
        AssertEx.Contains(warnings[0].Message, "model-a", StringComparison.Ordinal);
        AssertEx.Contains(warnings[0].Message, $"pid {handle.ProcessId}", StringComparison.Ordinal);
    }

    [Test]
    public async Task WaitForProcessExit_LiveProcess_TimesOutFalse_AndNoProcess_ReturnsTrue()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);
        AssertEx.True(await supervisor.WaitForProcessExitAsync("model-a", ModelRole.Embedding, TimeSpan.Zero, CancellationToken.None));

        _ = await supervisor.EnsureRunningAsync("model-a", ModelRole.Embedding, CancellationToken.None);

        AssertEx.False(await supervisor.WaitForProcessExitAsync("model-a", ModelRole.Embedding, TimeSpan.Zero, CancellationToken.None));
        AssertEx.False(launcher.Handles.Single().WasTreeKilled, "Waiting must never tear down a live process.");
    }

    [Test]
    public async Task EnsureRunning_SpawnAlwaysFails_RetriesToCap_ThenSurfacesSanitized()
    {
        var attempts = 0;
        var launcher = new FakeProcessLauncher(_ =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("internal failure with /secret/path/model.gguf and TOKEN=abc123");
        });
        await using var supervisor = SupervisorFactory.Create(launcher,
            options: new LlamaServerSupervisorOptions
            {
                MaxRestartAttempts = 3,
                IdleTimeToLive = TimeSpan.FromHours(1)
            });

        var ex = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None));

        AssertEx.Equal(expected: 3, attempts); // retried up to the restart cap.
        // Sanitized surface: no internal path or secret leaks into the user-facing message.
        AssertEx.False(ex.Message.Contains("/secret/path", StringComparison.OrdinalIgnoreCase));
        AssertEx.False(ex.Message.Contains("TOKEN", StringComparison.OrdinalIgnoreCase));
        AssertEx.Contains(ex.Message, "failed to start", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task EnsureRunning_ReadinessNeverReady_SurfacesSanitized()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher,
            new FakeHealthProbe(false),
            options: new LlamaServerSupervisorOptions
            {
                MaxRestartAttempts = 2,
                IdleTimeToLive = TimeSpan.FromHours(1)
            });

        var ex = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None));

        // A readiness timeout now surfaces its own classified, sanitized message (retried at most
        // MaxReadinessTimeoutRetries times) rather than collapsing into the generic "failed to start" wrapper.
        AssertEx.Contains(ex.Message, "did not become ready", StringComparison.OrdinalIgnoreCase);
        // Every failed start's half-spawned process must be torn down — no leaked handles.
        AssertEx.True(launcher.Handles.All(h => h.WasTreeKilled), "Failed-readiness spawns must be tree-killed.");
    }

    [Test]
    public async Task EnsureRunning_ProcessExitsDuringLoad_FailsFastWithoutRetry()
    {
        // A model whose llama-server crashes during context creation: the child exits almost immediately while
        // /health never becomes ready. The supervisor must observe the exit and fail fast — not poll /health for the
        // full readiness budget and then retry the guaranteed-to-re-crash spawn MaxRestartAttempts times.
        var launcher = new FakeProcessLauncher(_ =>
        {
#pragma warning disable CA2000 // Ownership transfers to the supervisor (via the launcher fake), which disposes it on teardown.
            var handle = new FakeProcessHandle(3000);
#pragma warning restore CA2000
            handle.SimulateExit(); // died during load, before readiness.
            return handle;
        });

        // GatedHealthProbe never becomes ready (never Release()d), so ONLY the process-exit race can complete the wait.
        await using var supervisor = SupervisorFactory.Create(launcher,
            new GatedHealthProbe(),
            options: new LlamaServerSupervisorOptions
            {
                MaxRestartAttempts = 3,
                IdleTimeToLive = TimeSpan.FromHours(1)
            });

        var ex = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => supervisor.EnsureRunningAsync("crashy-model", ModelRole.Chat, CancellationToken.None));

        AssertEx.Equal(expected: 1, launcher.LaunchCount); // non-retryable: one spawn, NOT MaxRestartAttempts (3).
        AssertEx.Contains(ex.Message, "exited while loading", StringComparison.OrdinalIgnoreCase);
        // No internal path leaks, and the dead child is reaped so its port is freed.
        AssertEx.False(ex.Message.Contains("/fake/", StringComparison.OrdinalIgnoreCase));
        AssertEx.True(launcher.Handles.All(h => h.WasTreeKilled), "The crashed child must be tree-killed.");
    }

    [Test]
    public async Task EnsureRunning_ModelNotInstalled_SurfacesSanitized()
    {
        await using var supervisor = SupervisorFactory.Create(modelStore: new FakeModelStore(null));

        var ex = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => supervisor.EnsureRunningAsync("ghost", ModelRole.Chat, CancellationToken.None));

        AssertEx.Contains(ex.Message, "not installed", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task EnsureRunning_ExternalEndpointConfigured_AttachesWithoutSpawning()
    {
        var launcher = new FakeProcessLauncher();
        var external = new LlamaServerExternalEndpointOptions
        {
            ChatEndpointsByModel = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase)
            {
                ["remote-model"] = new("http://127.0.0.1:9999/v1")
            }
        };
        await using var supervisor = SupervisorFactory.Create(launcher, externalEndpoints: external);

        var endpoint = await supervisor.EnsureRunningAsync("remote-model", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal("http://127.0.0.1:9999/v1", endpoint.BaseAddress.AbsoluteUri);
        AssertEx.Equal(expected: 0, launcher.LaunchCount); // hybrid attach: no local process spawned.
    }

    [Test]
    public void ExternalEndpointResolve_RerankerDoesNotFallThroughToChat()
    {
        var external = new LlamaServerExternalEndpointOptions
        {
            ChatEndpointsByModel = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase)
            {
                ["remote-model"] = new("http://127.0.0.1:9999/v1")
            }
        };

        AssertEx.Null(external.Resolve("remote-model", ModelRole.Reranker));
    }

    [Test]
    public async Task CheckHealth_AggregatesPerProcessDiagnostics()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher,
            new FakeHealthProbe(responsive: true));

        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Embedding, CancellationToken.None);

        var healths = await supervisor.CheckHealthAsync(CancellationToken.None);

        AssertEx.Equal(expected: 2, healths.Count);
        AssertEx.True(healths.All(h => h.IsResponsive));
        AssertEx.False(healths.Any(h => h.HasExited));
        AssertEx.Contains(healths, h => h.Role == ModelRole.Chat);
        AssertEx.Contains(healths, h => h.Role == ModelRole.Embedding);
    }

    [Test]
    public async Task CheckHealth_MarksACrashedProcessAsExited()
    {
        // A crashed handle lingers in the process table until the idle reaper collects it. `IsResponsive: false` alone
        // cannot tell that apart from a live process that is loading or wedged, and the two are opposite answers for a
        // caller deciding capacity: a corpse holds no VRAM and no slot, a wedged process holds both. `HasExited` is
        // the difference, and the capacity snapshot filters on it.
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher,
            new FakeHealthProbe(responsive: true));

        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        launcher.Handles.Single().SimulateExit();

        var health = (await supervisor.CheckHealthAsync(CancellationToken.None)).Single();

        AssertEx.True(health.HasExited);
        AssertEx.False(health.IsResponsive);
    }
}
