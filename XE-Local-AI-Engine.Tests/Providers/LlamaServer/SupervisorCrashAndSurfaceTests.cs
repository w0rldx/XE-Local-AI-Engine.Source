namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies that a repeatedly-failing spawn retries up to the restart cap with backoff and then surfaces a
///     sanitized <see cref="LlamaRuntimeException" /> (no internal paths/secrets); a not-installed model surfaces the
///     same way. Also covers the hybrid external-endpoint attach path (attach to a configured endpoint instead of
///     spawning a local process) and the per-process health aggregation surface.
/// </summary>
public sealed class SupervisorCrashAndSurfaceTests
{
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
