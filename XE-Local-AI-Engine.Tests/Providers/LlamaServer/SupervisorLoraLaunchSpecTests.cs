namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A LoRA-adapter model launches as the BASE model with the adapter applied on top: <c>-m &lt;base&gt;</c> plus
///     <c>--lora &lt;adapter&gt;</c>. Flag name verified against the pinned llama.cpp release <c>b10201</c>.
/// </summary>
public sealed class SupervisorLoraLaunchSpecTests
{
    private static readonly LlamaServerProcessSupervisor.ProcessKey ChatKey = new("base:Q4_K_M+tuned", ModelRole.Chat);
    private static readonly LlamaServerProcessSupervisor.ProcessKey EmbeddingKey = new("embed+tuned", ModelRole.Embedding);

    [Test]
    public void LaunchSpec_WhenAdapterSupplied_EmitsLoraAgainstTheBaseModel()
    {
        var spec = LlamaServerProcessSupervisor.BuildLaunchSpec(ChatKey,
            "/fake/bin/llama-server",
            "/fake/models/base.gguf",
            port: 8080,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            chatCacheReuse: 256,
            adapterFilePath: "/fake/models/tuned-adapter.gguf");

        AssertEx.Equal("/fake/models/base.gguf", spec.Arguments[IndexOf(spec.Arguments, "-m") + 1]);
        AssertEx.Equal("/fake/models/tuned-adapter.gguf", spec.Arguments[IndexOf(spec.Arguments, "--lora") + 1]);
    }

    [Test]
    public void LaunchSpec_WhenNoAdapter_EmitsNoLoraFlag()
    {
        var spec = LlamaServerProcessSupervisor.BuildLaunchSpec(ChatKey,
            "/fake/bin/llama-server",
            "/fake/models/base.gguf",
            port: 8080,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            chatCacheReuse: 256);

        AssertEx.False(spec.Arguments.Contains("--lora"), "A model with no adapter must get no --lora flag.");
    }

    [Test]
    public void LaunchSpec_WhenAdapterOnPooledRole_StillEmitsLora()
    {
        // An adapter changes the weights, not the serving mode, so it is not gated on the chat role the way --mmproj is.
        var spec = LlamaServerProcessSupervisor.BuildLaunchSpec(EmbeddingKey,
            "/fake/bin/llama-server",
            "/fake/models/base.gguf",
            port: 8080,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            chatCacheReuse: 0,
            adapterFilePath: "/fake/models/tuned-adapter.gguf");

        AssertEx.Equal("/fake/models/tuned-adapter.gguf", spec.Arguments[IndexOf(spec.Arguments, "--lora") + 1]);
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
