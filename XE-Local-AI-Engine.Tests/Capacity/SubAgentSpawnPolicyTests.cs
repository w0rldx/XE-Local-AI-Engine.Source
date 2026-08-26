namespace XE_Local_AI_Engine.Tests.Capacity;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class SubAgentSpawnPolicyTests
{
    [Test]
    public void BindingPolicy_RequiresExactlyOneBinding()
    {
        AssertEx.True(SubAgentSpawnPolicy.HasExactlyOneBinding(new SubAgentSpawnRequest { ModelId = "model" }));
        AssertEx.True(SubAgentSpawnPolicy.HasExactlyOneBinding(new SubAgentSpawnRequest { SubAgentKey = "agent" }));
        AssertEx.False(SubAgentSpawnPolicy.HasExactlyOneBinding(new SubAgentSpawnRequest()));
        AssertEx.False(SubAgentSpawnPolicy.HasExactlyOneBinding(new SubAgentSpawnRequest { ModelId = "model", SubAgentKey = "agent" }));
    }

    [Test]
    public void ChildToolPolicy_StripsSpawnAndApprovalTools_WithoutUnwrappingApproval()
    {
        var spawn = AIFunctionFactory.Create((string input) => input, "spawn_subagent");
        var approvalInner = AIFunctionFactory.Create((string input) => input, "read_file");
        var approval = new ApprovalRequiredAIFunction(approvalInner);
        var safe = AIFunctionFactory.Create((string input) => input, "list_files");

        var curated = AssertEx.NotNull(SubAgentSpawnPolicy.RemoveUnsupportedChildTools([spawn, approval, safe], out var dropped));

        AssertEx.Equal(1, curated.Count);
        AssertEx.Equal("list_files", curated[0].Name);
        AssertEx.Equal(1, dropped.Count);
        AssertEx.Equal("read_file", dropped[0]);
    }

    [Test]
    public void CoderToolPolicy_RequiresExactDistinctReadOnlySet()
    {
        AssertEx.True(SubAgentSpawnPolicy.HasExactCoderToolNames(["read_file", "list_files", "search_text"]));
        AssertEx.False(SubAgentSpawnPolicy.HasExactCoderToolNames(["read_file", "list_files", "search_text", "write_file"]));
        AssertEx.False(SubAgentSpawnPolicy.HasExactCoderToolNames(["read_file", "read_file", "search_text"]));
    }
}
