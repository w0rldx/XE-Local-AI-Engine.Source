namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
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
        await using var supervisor = SupervisorFactory.Create(
            launcher: launcher,
            options: new LlamaServerSupervisorOptions { MaxRestartAttempts = 3, IdleTimeToLive = TimeSpan.FromHours(1) });

        var ex = await AssertEx.ThrowsAsync<LlamaRuntimeException>(
            () => supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None));

        AssertEx.Equal(3, attempts); // retried up to the restart cap.
        // Sanitized surface: no internal path or secret leaks into the user-facing message.
        AssertEx.False(ex.Message.Contains("/secret/path", StringComparison.OrdinalIgnoreCase));
        AssertEx.False(ex.Message.Contains("TOKEN", StringComparison.OrdinalIgnoreCase));
        AssertEx.Contains(ex.Message, "failed to start", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task EnsureRunning_ReadinessNeverReady_SurfacesSanitized()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(
            launcher: launcher,
            healthProbe: new FakeHealthProbe(ready: false),
            options: new LlamaServerSupervisorOptions { MaxRestartAttempts = 2, IdleTimeToLive = TimeSpan.FromHours(1) });

        var ex = await AssertEx.ThrowsAsync<LlamaRuntimeException>(
            () => supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None));

        AssertEx.Contains(ex.Message, "failed to start", StringComparison.OrdinalIgnoreCase);
        // Every failed start's half-spawned process must be torn down — no leaked handles.
        AssertEx.True(launcher.Handles.All(h => h.WasTreeKilled), "Failed-readiness spawns must be tree-killed.");
    }

    [Test]
    public async Task EnsureRunning_ModelNotInstalled_SurfacesSanitized()
    {
        await using var supervisor = SupervisorFactory.Create(modelStore: new FakeModelStore(fixedPath: null));

        var ex = await AssertEx.ThrowsAsync<LlamaRuntimeException>(
            () => supervisor.EnsureRunningAsync("ghost", ModelRole.Chat, CancellationToken.None));

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
        await using var supervisor = SupervisorFactory.Create(launcher: launcher, externalEndpoints: external);

        var endpoint = await supervisor.EnsureRunningAsync("remote-model", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal("http://127.0.0.1:9999/v1", endpoint.BaseAddress.AbsoluteUri);
        AssertEx.Equal(0, launcher.LaunchCount); // hybrid attach: no local process spawned.
    }

    [Test]
    public async Task CheckHealth_AggregatesPerProcessDiagnostics()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(
            launcher: launcher,
            healthProbe: new FakeHealthProbe(responsive: true));

        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Embedding, CancellationToken.None);

        var healths = await supervisor.CheckHealthAsync(CancellationToken.None);

        AssertEx.Equal(2, healths.Count);
        AssertEx.True(healths.All(h => h.IsResponsive));
        AssertEx.Contains(healths, h => h.Role == ModelRole.Chat);
        AssertEx.Contains(healths, h => h.Role == ModelRole.Embedding);
    }
}
