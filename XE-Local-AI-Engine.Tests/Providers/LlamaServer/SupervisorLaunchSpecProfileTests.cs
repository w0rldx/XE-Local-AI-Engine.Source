namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Keystone coverage for the profile-driven launch-spec seam: the supervisor no longer forces
///     <c>--n-gpu-layers 999</c>. A GPU spawn with no frozen profile emits <c>--fit on</c> + <c>--metrics</c> (auto-fit);
///     a replay profile emits its explicit <c>-c/-ngl/-ts/-ot</c> (and matched <c>-ctk/-ctv</c> + <c>--flash-attn</c>)
///     verbatim with NO <c>--fit</c>; the CPU variant emits no gpu/fit args at all. Flag names verified against the
///     pinned llama.cpp release <c>b9692</c> (<c>--fit</c>, <c>--metrics</c>, <c>-c</c>, <c>--n-gpu-layers</c>,
///     <c>-ts</c>, <c>-ot</c>, <c>-ctk/-ctv</c>, <c>--flash-attn</c>).
/// </summary>
public sealed class SupervisorLaunchSpecProfileTests
{
    private static readonly LlamaServerProcessSupervisor.ProcessKey ChatKey = new("llama3", ModelRole.Chat);

    [Test]
    public void LaunchSpec_WhenExploreMode_EmitsFitOnAndMetrics_NoExplicitFitArgs()
    {
        var spec = BuildGpuSpec(ResolvedLaunchArguments.Explore());

        AssertEx.Contains(spec.Arguments, "--fit");
        AssertEx.Equal("on", spec.Arguments[IndexOf(spec.Arguments, "--fit") + 1]);
        AssertEx.Contains(spec.Arguments, "--metrics");
        // Any explicit fit-arg disables llama.cpp auto-fit, so explore mode must emit none of them.
        AssertEx.False(spec.Arguments.Contains("-c"), "Explore mode must not emit an explicit -c.");
        AssertEx.False(spec.Arguments.Contains("--n-gpu-layers"), "Explore mode must not emit an explicit -ngl.");
        AssertEx.False(spec.Arguments.Contains("-ts"), "Explore mode must not emit an explicit -ts.");
        AssertEx.False(spec.Arguments.Contains("-ot"), "Explore mode must not emit an explicit -ot.");
        AssertEx.False(spec.Arguments.Contains("999"), "The forced -ngl 999 placement is removed.");
    }

    [Test]
    public void LaunchSpec_WhenReplayProfile_ReplaysArgsVerbatim_NoFit()
    {
        var resolved = ResolvedLaunchArguments.Replay(ctxSize: 8192,
            nGpuLayers: 24,
            tensorSplit: "0.6,0.4",
            overrideTensor: "exps=CPU");

        var spec = BuildGpuSpec(resolved);

        AssertEx.Equal("8192", spec.Arguments[IndexOf(spec.Arguments, "-c") + 1]);
        AssertEx.Equal("24", spec.Arguments[IndexOf(spec.Arguments, "--n-gpu-layers") + 1]);
        AssertEx.Equal("0.6,0.4", spec.Arguments[IndexOf(spec.Arguments, "-ts") + 1]);
        AssertEx.Equal("exps=CPU", spec.Arguments[IndexOf(spec.Arguments, "-ot") + 1]);
        // Replay and auto-fit are mutually exclusive per run — never emit --fit when replaying frozen args.
        AssertEx.False(spec.Arguments.Contains("--fit"), "A replayed profile must not emit --fit.");
        AssertEx.False(spec.Arguments.Contains("999"), "A replayed profile must not carry the old forced -ngl 999.");
    }

    [Test]
    public void LaunchSpec_WhenKvQuant_RequiresFlashAttnAndMatchingTypes()
    {
        var resolved = ResolvedLaunchArguments.Replay(ctxSize: 4096,
            nGpuLayers: 32,
            kvTypeK: "q8_0",
            kvTypeV: "q8_0",
            flashAttn: true);

        var spec = BuildGpuSpec(resolved);

        var kvKey = spec.Arguments[IndexOf(spec.Arguments, "-ctk") + 1];
        var kvValue = spec.Arguments[IndexOf(spec.Arguments, "-ctv") + 1];
        AssertEx.Equal("q8_0", kvKey);
        AssertEx.Equal(kvKey, kvValue); // matching-type rule
        AssertEx.Contains(spec.Arguments, "--flash-attn");
        AssertEx.Equal("on", spec.Arguments[IndexOf(spec.Arguments, "--flash-attn") + 1]);
    }

    [Test]
    public async Task LaunchSpec_Replay_RejectsMismatchedKvTypes_AndKvWithoutFlashAttn()
    {
        // Matching-type rule: one KV type without the other is rejected.
        await AssertEx.ThrowsAsync<ArgumentException>(() =>
        {
            _ = ResolvedLaunchArguments.Replay(ctxSize: 4096, kvTypeK: "q8_0", flashAttn: true);
            return Task.CompletedTask;
        });

        // Flash-attention invariant: quantized/explicit KV requires --flash-attn.
        await AssertEx.ThrowsAsync<ArgumentException>(() =>
        {
            _ = ResolvedLaunchArguments.Replay(ctxSize: 4096, kvTypeK: "q8_0", kvTypeV: "q8_0", flashAttn: false);
            return Task.CompletedTask;
        });
    }

    [Test]
    public void LaunchSpec_AlwaysEmitsSingleSlotAndNoWarmup()
    {
        // Single-slot serving (locked design) + skip the empty-run warmup so a large model becomes ready before the
        // readiness budget elapses (otherwise it tree-kills + respawns). Both apply to every spawn regardless of mode.
        var explore = BuildGpuSpec(ResolvedLaunchArguments.Explore());
        AssertEx.Equal("1", explore.Arguments[IndexOf(explore.Arguments, "--parallel") + 1]);
        AssertEx.Contains(explore.Arguments, "--no-warmup");

        var cpu = LlamaServerProcessSupervisor.BuildLaunchSpec(ChatKey,
            "/fake/bin/llama-server",
            "/fake/models/model.gguf",
            port: 8080,
            GpuVariant.Cpu,
            ResolvedLaunchArguments.Explore());
        AssertEx.Equal("1", cpu.Arguments[IndexOf(cpu.Arguments, "--parallel") + 1]);
        AssertEx.Contains(cpu.Arguments, "--no-warmup");
    }

    [Test]
    public void LaunchSpec_WhenCpuVariant_EmitsNoGpuOrFitArgs()
    {
        // Even with a replay profile, the CPU variant stays a pure CPU run: no gpu/fit args at all.
        var resolved = ResolvedLaunchArguments.Replay(ctxSize: 8192, nGpuLayers: 24);
        var spec = LlamaServerProcessSupervisor.BuildLaunchSpec(ChatKey,
            "/fake/bin/llama-server",
            "/fake/models/model.gguf",
            port: 8080,
            GpuVariant.Cpu,
            resolved);

        AssertEx.False(spec.Arguments.Contains("--fit"), "CPU must not emit --fit.");
        AssertEx.False(spec.Arguments.Contains("--metrics"), "CPU must not emit --metrics.");
        AssertEx.False(spec.Arguments.Contains("--n-gpu-layers"), "CPU must not emit -ngl.");
        AssertEx.False(spec.Arguments.Contains("-c"), "CPU must not emit the replay -c (gpu/fit block is GPU-only).");
        AssertEx.Contains(spec.Arguments, "--jinja"); // mandatory chat flag stays
    }

    [Test]
    public async Task SpawnPath_AwaitsResolver_AndAppliesReplayArgs()
    {
        var launcher = new FakeProcessLauncher();
        var resolver = new FakeInferenceProfileResolver(ResolvedLaunchArguments.Replay(ctxSize: 4096, nGpuLayers: 20));
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            profileResolver: resolver);

        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        // The supervisor awaited the resolver for this (model, role, backend) on the spawn path.
        AssertEx.True(resolver.Calls.TryDequeue(out var call));
        AssertEx.Equal("llama3", call.ModelName);
        AssertEx.Equal(ModelRole.Chat, call.Role);
        AssertEx.Equal(GpuVariant.Cuda, call.Backend);

        // And threaded the resolved replay args into the launched spec (no auto-fit, no forced 999).
        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Equal("4096", spec!.Arguments[IndexOf(spec.Arguments, "-c") + 1]);
        AssertEx.Equal("20", spec.Arguments[IndexOf(spec.Arguments, "--n-gpu-layers") + 1]);
        AssertEx.False(spec.Arguments.Contains("--fit"), "Replay spawn must not emit --fit.");
        AssertEx.False(spec.Arguments.Contains("999"), "Replay spawn must not carry the old forced -ngl 999.");
    }

    private static LlamaServerLaunchSpec BuildGpuSpec(ResolvedLaunchArguments resolved)
    {
        return LlamaServerProcessSupervisor.BuildLaunchSpec(ChatKey,
            "/fake/bin/llama-server",
            "/fake/models/model.gguf",
            port: 8080,
            GpuVariant.Cuda,
            resolved);
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
}
