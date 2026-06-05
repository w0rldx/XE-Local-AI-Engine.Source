namespace XE_Local_AI_Engine.Tests.Chat;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LocalChatMapperPartsTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public void ToResponse_WhenMessageHasParts_CarriesOrderedInterleaveOntoTheResponse()
    {
        var parts = new List<NodeChatMessagePart>
        {
            new(NodeChatMessagePartKinds.Reasoning, 0, Text: "before"),
            new(NodeChatMessagePartKinds.Tool, 1, ToolCallId: "call-1", Name: "GetCurrentTime", State: NodeChatToolPartStates.Received, Args: "{}", Result: "now"),
            new(NodeChatMessagePartKinds.Reasoning, 2, Text: "after")
        };
        var message = BuildMessage(parts);

        var response = message.ToResponse();

        var responseParts = AssertEx.NotNull(response.Parts);
        AssertEx.Equal(3, responseParts.Count);
        AssertEx.Equal(NodeChatMessagePartKinds.Tool, responseParts[1].Kind);
        AssertEx.Equal("call-1", responseParts[1].ToolCallId);
        AssertEx.Equal("now", responseParts[1].Result);
        AssertEx.Equal(NodeChatMessagePartKinds.Reasoning, responseParts[2].Kind);
        AssertEx.Equal("after", responseParts[2].Text);
    }

    [Test]
    public void ToResponse_WhenMessageHasNoParts_ReturnsNullParts()
    {
        var message = BuildMessage(parts: null);

        var response = message.ToResponse();

        AssertEx.Null(response.Parts);
    }

    [Test]
    public void ToResponse_WhenSerializedWithWebDefaults_EmitsCamelCaseParts()
    {
        var parts = new List<NodeChatMessagePart>
        {
            new(NodeChatMessagePartKinds.Tool, 0, ToolCallId: "call-1", Name: "GetCurrentTime", State: NodeChatToolPartStates.Received, Args: "{}", Result: "now", RequiresApproval: false)
        };
        var response = BuildMessage(parts).ToResponse();

        // The endpoint serializes responses with Web JSON defaults (camelCase) — assert the wire field names the
        // frontend reads ("parts", "kind", "sequence", "toolCallId", ...) appear exactly.
        var json = JsonSerializer.Serialize(response, WebOptions);

        AssertEx.Contains(json, "\"parts\":");
        AssertEx.Contains(json, "\"kind\":\"tool\"");
        AssertEx.Contains(json, "\"sequence\":0");
        AssertEx.Contains(json, "\"toolCallId\":\"call-1\"");
        AssertEx.Contains(json, "\"name\":\"GetCurrentTime\"");
        AssertEx.Contains(json, "\"state\":\"received\"");
        AssertEx.Contains(json, "\"result\":\"now\"");
        AssertEx.Contains(json, "\"requiresApproval\":false");
    }

    [Test]
    public void ToResponse_SurfacesGenerationDurationMs()
    {
        var message = BuildMessage(parts: null) with { GenerationDurationMs = 2000 };

        var response = message.ToResponse();

        AssertEx.Equal(2000L, response.GenerationDurationMs);

        // The frontend reads "generationDurationMs" off the camelCase wire response to compute tokens/sec.
        var json = JsonSerializer.Serialize(response, WebOptions);
        AssertEx.Contains(json, "\"generationDurationMs\":2000");
    }

    [Test]
    public void ToResponse_WhenNoGenerationDuration_ReturnsNull()
    {
        var response = BuildMessage(parts: null).ToResponse();

        AssertEx.Null(response.GenerationDurationMs);
    }

    private static NodeChatPersistedMessageDto BuildMessage(IReadOnlyList<NodeChatMessagePart>? parts)
    {
        return new NodeChatPersistedMessageDto(Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            "assistant",
            "the answer",
            "before\nafter",
            NodeChatMessageStatusValues.Completed,
            1,
            2,
            "llama",
            null,
            null,
            Parts: parts);
    }
}
