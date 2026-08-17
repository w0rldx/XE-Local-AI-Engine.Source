namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

public interface IBenchmarkEligibilityPolicy
{
    ResolvedAgentRuntime Apply(ResolvedAgentRuntime runtime);
}

public sealed class BenchmarkEligibilityPolicy : IBenchmarkEligibilityPolicy
{
    public ResolvedAgentRuntime Apply(ResolvedAgentRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.Kind != AgentDefinitionKind.Single)
        {
            throw new BenchmarkEligibilityException("Only Single agent definitions are eligible for benchmarks.");
        }

        if (runtime.ModelProfile is not null)
        {
            throw new BenchmarkEligibilityException("Benchmark resolution must suppress the agent model profile.");
        }

        var tools = runtime.AllowedTools.Where(static tool => !string.Equals(tool.Name, AskUserTool.ToolName, StringComparison.Ordinal)).ToArray();
        if (tools.Any(static tool => tool.Category != ToolCategory.ReadLocal || tool.RequiresApproval))
        {
            throw new BenchmarkEligibilityException("The resolved agent tool offer is not safe for unattended benchmark execution.");
        }

        return runtime with
        {
            AllowedTools = tools
        };
    }
}

public sealed class BenchmarkEligibilityException(string message) : InvalidOperationException(message);

internal static class BenchmarkModelEligibility
{
    /// <summary>
    ///     Admits local llama.cpp chat GGUFs only. An attached <c>mmproj</c> projector member is NOT disqualifying:
    ///     the HF acquisition path auto-attaches one to modern text models (gemma-4, Qwen3.x), and it is an optional
    ///     companion the chat runtime passes as <c>--mmproj</c> without changing text generation. The benchmark itself
    ///     stays text-only — it never sends image content — so a projector-bearing chat model measures the same as a
    ///     bare one. Genuine vision/projector-only models are excluded by their <see cref="GgufRole" />, not by this.
    /// </summary>
    public static void Validate(InstalledModelSnapshot snapshot, string role)
    {
        if (!string.Equals(snapshot.ProviderName, "llamacpp", StringComparison.OrdinalIgnoreCase)
            || snapshot.Role != GgufRole.Chat)
        {
            throw new BenchmarkEligibilityException($"The selected {role} model is not an eligible local text-generation GGUF.");
        }
    }
}
