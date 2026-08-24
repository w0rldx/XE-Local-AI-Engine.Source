namespace XE_Local_AI_Engine.AI.Agent.Tests.Tools;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The per-run tightening of the shared tool-result budget. The configured value is read once when the registries
///     are built, so a run that needs a smaller ceiling — a work-session step, whose knowledge-base reads return up to
///     50,000 characters each — can only get one ambiently. Tighten-only, so no run can raise the node's ceiling.
/// </summary>
public sealed class ToolResultBudgetScopeTests
{
    private const int NodeCeiling = 65_536;

    [Test]
    public async Task Invoke_InsideAScope_ClipsAFiftyKilobyteResultToTheScopeBudget()
    {
        var function = BuildTool(new string('k', 50_000), NodeCeiling);

        using (ToolResultBudgetScope.BeginScope(16_000))
        {
            var result = await function.InvokeAsync(new AIFunctionArguments());

            var text = result as string ?? throw new AssertionException("Expected a string result.");
            AssertEx.True(text.StartsWith(new string('k', 16_000), StringComparison.Ordinal), "The leading excerpt is kept.");
            AssertEx.Contains(text, "[truncated: 16000 of 50000 chars shown]", message: "The model has to be able to tell the read was clipped.");
        }
    }

    [Test]
    public async Task Invoke_WithNoScope_LeavesTheNodeBudgetInPlace()
    {
        var function = BuildTool(new string('k', 50_000), NodeCeiling);

        var result = await function.InvokeAsync(new AIFunctionArguments());

        AssertEx.Equal(expected: 50_000, (result as string)?.Length, "An ordinary chat turn must stay byte-identical: 50,000 is under the node ceiling.");
    }

    [Test]
    public async Task Invoke_WithAScopeLooserThanTheNodeBudget_KeepsTheNodeBudget()
    {
        var function = BuildTool(new string('k', 50_000), maxResultCharacters: 4_000);

        using (ToolResultBudgetScope.BeginScope(40_000))
        {
            var result = await function.InvokeAsync(new AIFunctionArguments());

            AssertEx.Contains(result as string, "[truncated: 4000 of 50000 chars shown]", message: "Tighten-only: a scope may never raise the node ceiling.");
        }
    }

    [Test]
    public void Scope_OnDispose_RestoresThePriorValueRatherThanClearingIt()
    {
        using (ToolResultBudgetScope.BeginScope(20_000))
        {
            using (ToolResultBudgetScope.BeginScope(5_000))
            {
                AssertEx.Equal(expected: 5_000, ToolResultBudgetScope.Current);
            }

            AssertEx.Equal(expected: 20_000, ToolResultBudgetScope.Current, "A nested scope must not leak into the outer turn.");
        }

        AssertEx.True(ToolResultBudgetScope.Current is null, "The outermost scope leaves no ambient value behind.");
    }

    private static AIFunction BuildTool(string output, int maxResultCharacters)
    {
        var options = new AgentToolPipelineOptions
        {
            MaxToolResultCharacters = maxResultCharacters
        };
        var registry = new ClientLocalToolRegistry([new FakeHandler(output)], Options.Create(options));

        _ = registry.TryResolve("read_document", out var tool);
        return tool as AIFunction ?? throw new AssertionException("Expected an AIFunction.");
    }

    private sealed class FakeHandler(string output) : IClientLocalToolHandler
    {
        public string ToolName => "read_document";

        public string Description => "Reads a knowledge-base document.";

        public string ParameterSchema => """{"type":"object"}""";

        public bool RequiresApproval => false;

        public Task<string> ExecuteAsync(string jsonArguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(output);
    }
}
