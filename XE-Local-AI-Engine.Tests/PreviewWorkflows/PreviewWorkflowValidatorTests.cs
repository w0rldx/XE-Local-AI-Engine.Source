namespace XE_Local_AI_Engine.Tests.PreviewWorkflows;

using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="PreviewWorkflowGraphValidator" /> tests: the canonical linear graph passes; every structural rule
///     (no Start / no End / cycle / fan-out / agent missing model / no agent between Start and End) is rejected.
/// </summary>
public sealed class PreviewWorkflowValidatorTests
{
    [Test]
    public void Validate_LinearGraph_IsValid()
    {
        var result = PreviewWorkflowGraphValidator.Validate(PreviewGraphBuilder.Linear());

        AssertEx.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Test]
    public void PreviewValidator_RejectsEmptyStartText()
    {
        var graph = PreviewGraphBuilder.Linear() with
        {
            StartText = "   "
        };

        AssertEx.False(PreviewWorkflowGraphValidator.Validate(graph).IsValid);
    }

    [Test]
    public void PreviewValidator_RejectsNoStart()
    {
        var graph = new PreviewWorkflowGraph
        {
            StartText = "x",
            Nodes = [PreviewGraphBuilder.Agent("agent"), PreviewGraphBuilder.End()],
            Edges = [PreviewGraphBuilder.Edge("agent", "end")]
        };

        AssertEx.False(PreviewWorkflowGraphValidator.Validate(graph).IsValid);
    }

    [Test]
    public void PreviewValidator_RejectsNoEnd()
    {
        var graph = new PreviewWorkflowGraph
        {
            StartText = "x",
            Nodes = [PreviewGraphBuilder.Start(), PreviewGraphBuilder.Agent("agent")],
            Edges = [PreviewGraphBuilder.Edge("start", "agent")]
        };

        AssertEx.False(PreviewWorkflowGraphValidator.Validate(graph).IsValid);
    }

    [Test]
    public void PreviewValidator_RejectsCycle()
    {
        // start → a → b → a (cycle) plus an unreachable end: in/out-degree on 'a' is 2 (fan-in) and the End is
        // unreachable, so this is rejected either way — the key property is the cyclic graph never validates.
        var graph = new PreviewWorkflowGraph
        {
            StartText = "x",
            Nodes = [PreviewGraphBuilder.Start(), PreviewGraphBuilder.Agent("a"), PreviewGraphBuilder.Agent("b"), PreviewGraphBuilder.End()],
            Edges =
            [
                PreviewGraphBuilder.Edge("start", "a"),
                PreviewGraphBuilder.Edge("a", "b"),
                PreviewGraphBuilder.Edge("b", "a")
            ]
        };

        AssertEx.False(PreviewWorkflowGraphValidator.Validate(graph).IsValid);
    }

    [Test]
    public void PreviewValidator_RejectsFanOut()
    {
        // start → agent, and agent fans out to BOTH b and end (out-degree 2). Acyclicity alone would permit this, but
        // linearity (out-degree ≤ 1) must reject it.
        var graph = new PreviewWorkflowGraph
        {
            StartText = "x",
            Nodes =
            [
                PreviewGraphBuilder.Start(),
                PreviewGraphBuilder.Agent("agent"),
                PreviewGraphBuilder.Agent("b"),
                PreviewGraphBuilder.End()
            ],
            Edges =
            [
                PreviewGraphBuilder.Edge("start", "agent"),
                PreviewGraphBuilder.Edge("agent", "b"),
                PreviewGraphBuilder.Edge("agent", "end")
            ]
        };

        var result = PreviewWorkflowGraphValidator.Validate(graph);

        AssertEx.False(result.IsValid);
        AssertEx.True(result.Errors.Any(static e => e.Contains("out-degree", StringComparison.Ordinal)),
            "expected an out-degree error for the fan-out node.");
    }

    [Test]
    public void PreviewValidator_RejectsNoModel()
    {
        var agentMissingModel = new PreviewWorkflowGraphNode
        {
            Id = "agent",
            Kind = PreviewWorkflowNodeKind.Agent,
            Label = "agent",
            Instructions = "Respond.",
            Model = null
        };
        var graph = new PreviewWorkflowGraph
        {
            StartText = "x",
            Nodes = [PreviewGraphBuilder.Start(), agentMissingModel, PreviewGraphBuilder.End()],
            Edges = [PreviewGraphBuilder.Edge("start", "agent"), PreviewGraphBuilder.Edge("agent", "end")]
        };

        var result = PreviewWorkflowGraphValidator.Validate(graph);

        AssertEx.False(result.IsValid);
        AssertEx.True(result.Errors.Any(static e => e.Contains("model", StringComparison.OrdinalIgnoreCase)),
            "expected a missing-model error.");
    }

    [Test]
    public void PreviewValidator_RejectsNoAgentBetween()
    {
        // start → end with no agent: a 400, never a no-op.
        var graph = new PreviewWorkflowGraph
        {
            StartText = "x",
            Nodes = [PreviewGraphBuilder.Start(), PreviewGraphBuilder.End()],
            Edges = [PreviewGraphBuilder.Edge("start", "end")]
        };

        var result = PreviewWorkflowGraphValidator.Validate(graph);

        AssertEx.False(result.IsValid);
        AssertEx.True(result.Errors.Any(static e => e.Contains("Agent", StringComparison.Ordinal)),
            "expected a 'no agent between Start and End' error.");
    }
}
