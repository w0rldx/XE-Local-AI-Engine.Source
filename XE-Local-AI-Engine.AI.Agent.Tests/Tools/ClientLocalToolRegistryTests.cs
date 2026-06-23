namespace XE_Local_AI_Engine.AI.Agent.Tests.Tools;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ClientLocalToolRegistryTests
{
    [Test]
    public void TryResolve_KnownHandler_ReturnsToolWithSchemaAndDescription()
    {
        var registry = new ClientLocalToolRegistry([new FakeHandler("run_in_agent_home", "Runs.", parameterSchema: """{"type":"object"}""", requiresApproval: false)]);

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
        var registry = new ClientLocalToolRegistry([new FakeHandler("run_in_agent_home", "Runs.", parameterSchema: """{"type":"object"}""", requiresApproval: true)]);

        _ = registry.TryResolve("run_in_agent_home", out var tool);

        AssertEx.True(tool is ApprovalRequiredAIFunction);
    }

    [Test]
    public void TryResolve_UnknownTool_ReturnsFalse()
    {
        var registry = new ClientLocalToolRegistry([]);

        var found = registry.TryResolve("missing", out var tool);

        AssertEx.False(found);
        AssertEx.True(tool is null);
    }

    private sealed class FakeHandler(string toolName, string description, string parameterSchema, bool requiresApproval)
        : IClientLocalToolHandler
    {
        public string ToolName => toolName;

        public string Description => description;

        public string ParameterSchema => parameterSchema;

        public bool RequiresApproval => requiresApproval;

        public Task<string> ExecuteAsync(string jsonArguments, CancellationToken cancellationToken = default)
        {
            return Task.FromResult("ok");
        }
    }
}
