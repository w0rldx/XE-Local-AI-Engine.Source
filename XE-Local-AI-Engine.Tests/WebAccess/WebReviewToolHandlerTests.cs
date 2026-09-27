namespace XE_Local_AI_Engine.Tests.WebAccess;

using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.WebAccess;
using XE_Local_AI_Engine.Client.Services.WebAccess.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The web tool handlers never reach the network: they return only what the result review stashed for their own
///     call id in their own invocation, and refuse every other way in.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class WebReviewToolHandlerTests
{
    [Test]
    [Arguments(WebFetchToolDefinition.ToolName)]
    [Arguments(WebSearchToolDefinition.ToolName)]
    public async Task ExecuteAsync_WithoutAReviewScope_RefusesWithoutFetching(string toolName)
    {
        using var server = new WebReviewTestServer();
        var handler = Handler(toolName, "call-1");

        var result = await handler.ExecuteAsync("""{"url":"https://news.example.com/tidal","query":"tidal"}""");

        AssertEx.Equal("not-reviewed", ErrorCode(result), "a direct invocation (graph headless path, training dataset) must be refused");
        AssertEx.Empty(server.Requests);
        AssertEx.True(handler.RequiresApproval, "the approval wrap is the pause the review gate builds on");
    }

    [Test]
    public async Task ExecuteAsync_ReturnsTheStashedResultForItsOwnCallOnce()
    {
        using var scope = WebReviewResultScope.BeginScope();
        WebReviewResultScope.Stash("call-1", """{"content":"reviewed"}""");
        var handler = Handler(WebFetchToolDefinition.ToolName, "call-1");

        AssertEx.Equal("""{"content":"reviewed"}""", await handler.ExecuteAsync("{}"));
        AssertEx.Equal("not-reviewed", ErrorCode(await handler.ExecuteAsync("{}")), "pop-once: a replayed call id gets nothing");
    }

    [Test]
    public async Task ExecuteAsync_WithABlankCallId_Refuses()
    {
        using var scope = WebReviewResultScope.BeginScope();
        WebReviewResultScope.Stash(string.Empty, """{"content":"reviewed"}""");

        var result = await Handler(WebSearchToolDefinition.ToolName, string.Empty).ExecuteAsync("{}");

        AssertEx.Equal("not-reviewed", ErrorCode(result), "without a call id nothing can be correlated, so nothing is returned");
    }

    [Test]
    public async Task ExecuteAsync_CannotSeeAnotherInvocationsStash()
    {
        var stashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var other = Task.Run(async () =>
        {
            using var otherScope = WebReviewResultScope.BeginScope();
            WebReviewResultScope.Stash("call-1", """{"content":"another conversation"}""");
            stashed.SetResult();
            await release.Task;
        });
        await stashed.Task;

        using var scope = WebReviewResultScope.BeginScope();
        var result = await Handler(WebFetchToolDefinition.ToolName, "call-1").ExecuteAsync("{}");
        release.SetResult();
        await other;

        AssertEx.Equal("not-reviewed", ErrorCode(result), "the same call id in a concurrent invocation must stay invisible");
    }

    [Test]
    public async Task ANestedScope_HidesTheOuterStashAndRestoresItOnDispose()
    {
        using var outer = WebReviewResultScope.BeginScope();
        WebReviewResultScope.Stash("call-1", "outer");

        using (WebReviewResultScope.BeginScope())
        {
            AssertEx.False(WebReviewResultScope.TryPop("call-1", out _), "a nested invocation must not read its parent's reviews");
        }

        AssertEx.Equal("outer", await Handler(WebFetchToolDefinition.ToolName, "call-1").ExecuteAsync("{}"));
    }

    [Test]
    public void BothTools_AreOfferedApprovalGatedInTheNetworkCategory()
    {
        foreach (var descriptor in WebAccessToolCatalog.Descriptors)
        {
            AssertEx.Equal(ToolCategory.Network, descriptor.Category);
            AssertEx.True(descriptor.RequiresApproval, $"{descriptor.Name} must carry the structural approval flag");
            AssertEx.True(WebAccessToolCatalog.IsWebTool(descriptor.Name));
        }

        AssertEx.False(WebAccessToolCatalog.IsWebTool(AskUserTool.ToolName));
    }

    [Test]
    public async Task Retriever_FetchesTheModelsUrlWithNoAllowList_AndSplitsPreviewFromFencedModelText()
    {
        using var server = new WebReviewTestServer();

        var retrieval = await server.CreateRetriever().RetrieveAsync(Call(WebFetchToolDefinition.ToolName, new { url = WebReviewTestServer.PageUrl }), CancellationToken.None);

        AssertEx.Equal(WebReviewTestServer.PageUrl, AssertEx.NotNull(server.Requests.Single()).AbsoluteUri, "the chat path is an open fetch: no allow-list refused the URL");
        var preview = AssertEx.NotNull(retrieval.Preview);
        AssertEx.Equal(WebReviewTestServer.PageText, preview.Text, "the preview is plain display text for the user");
        AssertEx.Equal(WebReviewTestServer.PageUrl, preview.FinalUrl);
        AssertEx.True(retrieval.ModelText.Contains(UntrustedContentFraming.UntrustedTrustLabel, StringComparison.Ordinal), "the model only ever gets the fenced form");
    }

    [Test]
    public async Task Retriever_ASearchPreviewCarriesTheBackendAndItsResults()
    {
        using var server = new WebReviewTestServer();

        var retrieval = await server.CreateRetriever().RetrieveAsync(Call(WebSearchToolDefinition.ToolName, new { query = "tidal power" }), CancellationToken.None);

        var preview = AssertEx.NotNull(retrieval.Preview);
        AssertEx.Equal("searxng", preview.Backend);
        AssertEx.Equal(WebReviewTestServer.SearchHitUrl, AssertEx.NotNull(preview.Results).Single().Url);
    }

    [Test]
    public async Task Retriever_WhenArgumentsDoNotMatchTheSchema_RefusesWithoutFetchingOrPreview()
    {
        using var server = new WebReviewTestServer();

        var retrieval = await server.CreateRetriever().RetrieveAsync(Call(WebFetchToolDefinition.ToolName, new { url = 5 }), CancellationToken.None);

        AssertEx.Equal("invalid-arguments", ErrorCode(retrieval.ModelText));
        AssertEx.Null(retrieval.Preview);
        AssertEx.Empty(server.Requests);
    }

    internal static FunctionCallContent Call(string toolName, object arguments, string callId = "call-1") =>
        new(callId, toolName, JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(arguments)));

    internal static string? ErrorCode(string result)
    {
        using var document = JsonDocument.Parse(result);
        return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    private static IClientLocalToolHandler Handler(string toolName, string callId) =>
        string.Equals(toolName, WebSearchToolDefinition.ToolName, StringComparison.Ordinal)
            ? new WebSearchToolHandler { ResolveCallId = () => callId }
            : new WebFetchToolHandler { ResolveCallId = () => callId };
}
