namespace XE_Local_AI_Engine.AI.Agent.Tests.Tools;

using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     One test per shape <c>McpClientTool</c> returns (<c>AIContent</c>, <c>AIContent[]</c>, or the whole
///     <c>CallToolResult</c> as JSON): the model must always get plain text, never the serialized result.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class McpToolResultProjectionAIFunctionTests
{
    [Test]
    public async Task Invoke_TextBlocks_ReachTheModelAsJoinedPlainText()
    {
        var sut = new McpToolResultProjectionAIFunction(Returning(new AIContent[] { new TextContent("line one"), new TextContent("line two") }));

        var result = await sut.InvokeAsync(new AIFunctionArguments());

        AssertEx.Equal("line one\nline two", result as string);
    }

    [Test]
    public void Project_StructuredContentMirroredByATextBlock_IsSentOnce()
    {
        var result = Project("""{"content":[{"type":"text","text":"{\"id\": 7, \"state\": \"open\"}"}],"structuredContent":{"id":7,"state":"open"}}""");

        AssertEx.Equal("{\"id\": 7, \"state\": \"open\"}", result);
    }

    [Test]
    public void Project_FastMcpResultWrapperAroundTheTextBlock_IsSentOnce()
    {
        // FastMCP wraps a plain-string return as structuredContent {"result": "<the text>"}: the live re-run saw a
        // 100k-character export go to the model twice, escaped, because the wrapper is not byte-equal to the text.
        var result = Project("""{"content":[{"type":"text","text":"line one\nline two"}],"structuredContent":{"result":"line one\nline two"}}""");

        AssertEx.Equal("line one\nline two", result);
    }

    [Test]
    public void Project_FastMcpResultWrapperAroundAJsonTextBlock_IsSentOnce()
    {
        var result = Project("""{"content":[{"type":"text","text":"[1, 2]"}],"structuredContent":{"result":[1,2]}}""");

        AssertEx.Equal("[1, 2]", result);
    }

    [Test]
    public void Project_ResultWrapperWithASecondProperty_IsNotTreatedAsAMirror()
    {
        var result = Project("""{"content":[{"type":"text","text":"ok"}],"structuredContent":{"result":"ok","took_ms":3}}""");

        AssertEx.Equal("ok\n{\"result\":\"ok\",\"took_ms\":3}", result);
    }

    [Test]
    public void Project_StructuredContentWithoutAText_IsAppendedOnce()
    {
        var result = Project("""{"content":[{"type":"text","text":"found one ticket"}],"structuredContent":{"id":7}}""");

        AssertEx.Equal("found one ticket\n{\"id\":7}", result);
    }

    [Test]
    public void Project_IsError_CarriesTheTypedFailurePrefix()
    {
        var result = Project("""{"content":[{"type":"text","text":"ticket 9 does not exist"}],"isError":true}""");

        AssertEx.Equal("[tool error: tool_reported] ticket 9 does not exist", result);
        AssertEx.Equal(ToolFailureText.ToolReportedCode, ToolFailureText.TryGetCode(result));
    }

    [Test]
    public void Project_ImageBlock_GivesTheModelAPlaceholderAndKeepsTheBytesForTheChat()
    {
        var bytes = new byte[34 * 1024];
        var result = Project($$$"""{"content":[{"type":"text","text":"chart:"},{"type":"image","data":"{{{Convert.ToBase64String(bytes)}}}","mimeType":"image/png"}],"_meta":{"k":1}}""");

        AssertEx.Equal("chart:\n[image image/png, 34 KB]", result);
        var image = ToolResultMedia.Get(result).Single();
        AssertEx.Equal("image/png", image.MediaType);
        AssertEx.Equal(bytes.Length, image.Data.Length);
    }

    [Test]
    public void Project_PlainPathDataContent_IsAPlaceholderNotATypeName()
    {
        var image = new DataContent(new byte[] { 1, 2, 3 }, "image/jpeg");

        var result = McpToolResultProjectionAIFunction.Project(image) as string;

        AssertEx.Equal("[image image/jpeg, 1 KB]", result);
        AssertEx.True(ReferenceEquals(image, ToolResultMedia.Get(result).Single()), "The DataContent itself must reach the chat for persistence.");
    }

    [Test]
    public void Project_AString_PassesThroughUntouched()
    {
        const string timeout = "[tool error: timeout] The MCP tool did not respond.";

        AssertEx.True(ReferenceEquals(timeout, McpToolResultProjectionAIFunction.Project(timeout)), "A wrapper's own text result must not be re-projected.");
    }

    private static string? Project(string callToolResultJson)
    {
        using var document = JsonDocument.Parse(callToolResultJson);
        return McpToolResultProjectionAIFunction.Project(document.RootElement.Clone()) as string;
    }

    private static AIFunction Returning(object result) => new FixedResultFunction(result);

    // Returns its object as is: AIFunctionFactory would marshal it, which is exactly the step McpClientTool skips.
    private sealed class FixedResultFunction : AIFunction
    {
        private readonly object _result;

        public FixedResultFunction(object result)
        {
            _result = result;
        }

        public override string Name => "mcp__srv__tool";

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) => new(_result);
    }
}
