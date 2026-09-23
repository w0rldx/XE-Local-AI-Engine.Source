namespace XE_Local_AI_Engine.Tests.Endpoints.GraphWorkflows.V1;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Tests.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The wire mapper's stored-document half: a Standard graph stays byte-identical at rest, and a Chat graph keeps both chat members.</summary>
[Category(TestCategories.Unit)]
public sealed class GraphWorkflowContractMapperTests
{
    [Test]
    public void ToGraphJson_OnAGraphWithoutAKind_EmitsNeitherKindNorChat()
    {
        var stored = GraphWorkflowContractMapper.ToGraphJson(GraphWorkflowContractMapper.ToWireGraph(GraphWorkflowGraphs.StartAgentEnd));

        using var document = JsonDocument.Parse(stored);
        AssertEx.False(document.RootElement.TryGetProperty("kind", out _), "a Standard graph gains no top-level kind at rest.");
        AssertEx.False(document.RootElement.TryGetProperty("chat", out _), "nor a chat block.");
        AssertEx.Equal(stored, GraphWorkflowContractMapper.ToGraphJson(GraphWorkflowContractMapper.ToWireGraph(stored)), "and a second round trip is byte-identical.");
    }

    [Test]
    public void ToGraphJson_OnAChatGraph_KeepsTheKindAndTheRawChatBlock()
    {
        var stored = GraphWorkflowContractMapper.ToGraphJson(GraphWorkflowContractMapper.ToWireGraph(GraphWorkflowGraphs.ChatInputAnswer));

        using var document = JsonDocument.Parse(stored);
        AssertEx.Equal("Chat", document.RootElement.GetProperty("kind").GetString());
        AssertEx.True(document.RootElement.GetProperty("chat").GetProperty("requireRerunConfirmation").GetBoolean());
    }
}
