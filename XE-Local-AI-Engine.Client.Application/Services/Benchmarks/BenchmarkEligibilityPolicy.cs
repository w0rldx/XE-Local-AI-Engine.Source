namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;

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
