namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies the supervisor's launch argument vector always carries the mandatory role flags — chat →
///     <c>--jinja</c>; embedding → <c>--embeddings</c> + a non-<c>none</c> <c>--pooling</c> value — and always binds
///     localhost only. Verified against the pinned llama.cpp release <c>b9692</c> flag names (<c>--jinja</c>,
///     <c>--embeddings</c>, <c>--pooling mean|cls|last</c>).
/// </summary>
public sealed class SupervisorSpawnArgsTests
{
    [Test]
    public async Task EnsureRunning_ChatRole_LaunchArgsContainJinja_AndBindLocalhost()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = NewSupervisor(launcher);

        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Contains(spec!.Arguments, "--jinja");
        AssertEx.False(spec.Arguments.Contains("--embeddings"), "Chat process must not enable embeddings.");
        AssertChatBindsLocalhost(spec);
    }

    [Test]
    public async Task EnsureRunning_EmbeddingRole_LaunchArgsContainEmbeddingsAndNonNonePooling()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = NewSupervisor(launcher);

        await supervisor.EnsureRunningAsync("nomic-embed", ModelRole.Embedding, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Contains(spec!.Arguments, "--embeddings");
        AssertEx.Contains(spec.Arguments, "--pooling");

        var poolingIndex = IndexOf(spec.Arguments, "--pooling");
        var poolingValue = spec.Arguments[poolingIndex + 1];
        AssertEx.Contains(new[]
        {
            "mean",
            "cls",
            "last"
        }, poolingValue);
        AssertEx.False(spec.Arguments.Contains("--jinja"), "Embedding process must not enable jinja chat templating.");
        AssertEx.False(string.Equals(poolingValue, "none", StringComparison.OrdinalIgnoreCase), "Pooling must not be none.");
    }

    [Test]
    public async Task EnsureRunning_RerankerRole_LaunchArgsContainRerankAndPoolingRank_NotEmbeddings()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = NewSupervisor(launcher);

        await supervisor.EnsureRunningAsync("bge-reranker-v2-m3", ModelRole.Reranker, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Contains(spec!.Arguments, "--rerank");
        AssertEx.Contains(spec.Arguments, "--pooling");

        var poolingIndex = IndexOf(spec.Arguments, "--pooling");
        AssertEx.Equal("rank", spec.Arguments[poolingIndex + 1]);

        // --rerank is mutually exclusive with --embeddings, and carries none of the chat-only flags.
        AssertEx.False(spec.Arguments.Contains("--embeddings"), "A rerank process must not enable embeddings.");
        AssertEx.False(spec.Arguments.Contains("--jinja"), "A rerank process must not enable jinja chat templating.");
    }

    [Test]
    public async Task EnsureRunning_GpuVariant_NoProfile_LaunchArgsEmitFitOnAndMetrics()
    {
        var launcher = new FakeProcessLauncher();
        // The default resolver returns explore-mode (no frozen profile), so a GPU spawn lets llama.cpp auto-fit drive
        // placement: --fit on + --metrics, NEVER the old forced --n-gpu-layers 999.
        await using var supervisor = SupervisorFactory.Create(launcher, variantSelector: new FakeVariantSelector(GpuVariant.Cuda));

        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Contains(spec!.Arguments, "--fit");
        var fitIndex = IndexOf(spec.Arguments, "--fit");
        AssertEx.Equal("on", spec.Arguments[fitIndex + 1]);
        AssertEx.Contains(spec.Arguments, "--metrics");
        AssertEx.False(spec.Arguments.Contains("--n-gpu-layers"), "Explore mode must not emit an explicit -ngl (it disables auto-fit).");
        AssertEx.False(spec.Arguments.Contains("999"), "The forced -ngl 999 placement is removed.");
    }

    [Test]
    public async Task EnsureRunning_CpuVariant_LaunchArgsOmitGpuAndFitArgs()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher, variantSelector: new FakeVariantSelector());

        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.False(spec!.Arguments.Contains("--n-gpu-layers"), "The CPU variant must not request GPU layer offload.");
        AssertEx.False(spec.Arguments.Contains("--fit"), "The CPU variant must not emit auto-fit args.");
        AssertEx.False(spec.Arguments.Contains("--metrics"), "The CPU variant must not emit --metrics.");
    }

    private static void AssertChatBindsLocalhost(LlamaServerLaunchSpec spec)
    {
        var hostIndex = IndexOf(spec.Arguments, "--host");
        AssertEx.Equal("127.0.0.1", spec.Arguments[hostIndex + 1]);
        AssertEx.Equal("127.0.0.1", spec.BaseAddress.Host);
        AssertEx.True(spec.BaseAddress.AbsoluteUri.EndsWith("/v1", StringComparison.Ordinal));
    }

    private static int IndexOf(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new AssertionException($"Expected flag '{flag}' in argument vector.");
    }

    private static LlamaServerProcessSupervisor NewSupervisor(FakeProcessLauncher launcher)
    {
        return SupervisorFactory.Create(launcher);
    }
}
