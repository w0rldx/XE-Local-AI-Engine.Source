namespace XE_Local_AI_Engine.AI.Agent.Tests.Tools;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ClientLocalToolRegistryTests
{
    [Test]
    public void TryResolve_KnownHandler_ReturnsToolWithSchemaAndDescription()
    {
        var registry = new ClientLocalToolRegistry(
            [new FakeHandler("run_in_agent_home", "Runs.", parameterSchema: """{"type":"object"}""", requiresApproval: false)],
            PipelineOptions());

        var found = registry.TryResolve("run_in_agent_home", out var tool);

        AssertEx.True(found);
        var function = tool as AIFunction ?? throw new AssertionException("Expected an AIFunction.");
        AssertEx.Equal("run_in_agent_home", function.Name);
        AssertEx.Equal("Runs.", function.Description);
        AssertEx.Equal("object", function.JsonSchema.GetProperty("type").GetString());
    }

    [Test]
    public void TryResolve_HighRiskHandler_WrapsInApprovalRequiredFunction()
    {
        var registry = new ClientLocalToolRegistry(
            [new FakeHandler("run_in_agent_home", "Runs.", parameterSchema: """{"type":"object"}""", requiresApproval: true)],
            PipelineOptions());

        _ = registry.TryResolve("run_in_agent_home", out var tool);

        AssertEx.True(tool is ApprovalRequiredAIFunction);
    }

    [Test]
    public void TryResolve_UnknownTool_ReturnsFalse()
    {
        var registry = new ClientLocalToolRegistry([], PipelineOptions());

        var found = registry.TryResolve("missing", out var tool);

        AssertEx.False(found);
        AssertEx.True(tool is null);
    }

    [Test]
    public void TryResolve_NonApprovalHandler_ResolvesBudgetedToolResult_NotApprovalWrapped()
    {
        // The coder read tools are RequiresApproval=false, so they must resolve to a plain (non-approval) executable.
        // The executable is now the shared BudgetedToolResultAIFunction backstop (which delegates name/schema to the
        // inner MetadataToolFunction), never an ApprovalRequiredAIFunction. This mirrors how the coder handlers resolve.
        var registry = new ClientLocalToolRegistry(
            [new FakeHandler("read_file", "Reads a file.", parameterSchema: """{"type":"object"}""", requiresApproval: false)],
            PipelineOptions());

        var found = registry.TryResolve("read_file", out var tool);

        AssertEx.True(found);
        AssertEx.False(tool is ApprovalRequiredAIFunction, "a read-only coder tool must not be approval-wrapped");
        AssertEx.True(tool is BudgetedToolResultAIFunction, "a non-approval handler resolves to the budgeted backstop");
    }

    [Test]
    public async Task InvokeAsync_OverBudgetResult_TruncatesWithMarker()
    {
        // A tool whose output exceeds the budget must be clipped with an explicit marker before it enters chat history.
        var registry = new ClientLocalToolRegistry(
            [new FakeHandler("read_file", "Reads a file.", parameterSchema: """{"type":"object"}""", requiresApproval: false, output: new string('x', 5_000))],
            PipelineOptions(maxToolResultCharacters: 1024));

        _ = registry.TryResolve("read_file", out var tool);
        var function = tool as AIFunction ?? throw new AssertionException("Expected an AIFunction.");

        var result = await function.InvokeAsync(new AIFunctionArguments());
        var text = result as string ?? throw new AssertionException("Expected a string result.");

        AssertEx.True(text.Contains("[truncated: 1024 of 5000 chars shown]", StringComparison.Ordinal), "the truncation marker must be present");
        AssertEx.True(text.Length < 5_000, "the result must be shorter than the original output");
    }

    [Test]
    public async Task InvokeAsync_WithinBudgetResult_ReturnsUnchanged()
    {
        var registry = new ClientLocalToolRegistry(
            [new FakeHandler("read_file", "Reads a file.", parameterSchema: """{"type":"object"}""", requiresApproval: false, output: "small")],
            PipelineOptions());

        _ = registry.TryResolve("read_file", out var tool);
        var function = tool as AIFunction ?? throw new AssertionException("Expected an AIFunction.");

        var result = await function.InvokeAsync(new AIFunctionArguments());

        AssertEx.Equal("small", result as string);
    }

    private static IOptions<AgentToolPipelineOptions> PipelineOptions(int? maxToolResultCharacters = null)
    {
        var options = new AgentToolPipelineOptions();
        if (maxToolResultCharacters is { } max)
        {
            options.MaxToolResultCharacters = max;
        }

        return Options.Create(options);
    }

    private sealed class FakeHandler(string toolName, string description, string parameterSchema, bool requiresApproval, string output = "ok")
        : IClientLocalToolHandler
    {
        public string ToolName => toolName;

        public string Description => description;

        public string ParameterSchema => parameterSchema;

        public bool RequiresApproval => requiresApproval;

        public Task<string> ExecuteAsync(string jsonArguments, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(output);
        }
    }
}
