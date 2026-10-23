namespace XE_Local_AI_Engine.Tests.CodexOAuth;

using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Proves the Codex Responses client surfaces provider usage, which the runner folds from
///     <see cref="UsageContent" /> on streamed updates, by replaying Responses bodies over a stub transport.
/// </summary>
/// <remarks>
///     The adapter reads <c>response.usage</c> from the terminal <c>response.completed</c> event, or from the response
///     JSON when not streaming, and maps <c>output_tokens_details.reasoning_tokens</c> to the dedicated
///     <see cref="UsageDetails.ReasoningTokenCount" />.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class CodexUsageReplayTests
{
    private const string ModelId = "gpt-5.4";

    private const string UsageJson =
        """{"input_tokens":120,"input_tokens_details":{"cached_tokens":0},"output_tokens":45,"output_tokens_details":{"reasoning_tokens":30},"total_tokens":165}""";

    [Test]
    public async Task Streaming_CompletedEventUsage_ArrivesAsOneUsageContent()
    {
        using var handler = new ReplayHandler(BuildStreamingBody(UsageJson), "text/event-stream");
        using var httpClient = new HttpClient(handler);
        using var client = BuildClient(httpClient);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            updates.Add(update);
        }

        AssertEx.Equal("hi there", string.Concat(updates.Select(u => u.Text)));
        var usage = updates.SelectMany(u => u.Contents).OfType<UsageContent>().Single();
        AssertEx.Equal(120L, usage.Details.InputTokenCount);
        AssertEx.Equal(45L, usage.Details.OutputTokenCount);
        AssertEx.Equal(165L, usage.Details.TotalTokenCount);
        AssertEx.Equal(30L, usage.Details.ReasoningTokenCount);
    }

    [Test]
    public async Task NonStreaming_ResponseUsage_PopulatesChatResponseUsage()
    {
        using var handler = new ReplayHandler(BuildResponseJson(UsageJson), "application/json");
        using var httpClient = new HttpClient(handler);
        using var client = BuildClient(httpClient);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        AssertEx.Equal("hi there", response.Text);
        var usage = AssertEx.NotNull(response.Usage, "the non-streaming response must carry usage");
        AssertEx.Equal(120L, usage.InputTokenCount);
        AssertEx.Equal(45L, usage.OutputTokenCount);
        AssertEx.Equal(165L, usage.TotalTokenCount);
        AssertEx.Equal(30L, usage.ReasoningTokenCount);
    }

    private static IChatClient BuildClient(HttpClient httpClient)
    {
        // The same Responses client and store-disabling decorator the Codex factory composes, over a stub transport.
        var inner = CodexChatClientConstruction.Build(new Uri("https://chatgpt.com/backend-api/codex"), httpClient, ModelId);
        return new CodexStoreDisabledChatClient(inner, ModelId);
    }

    private static string BuildResponseJson(string usageJson)
    {
        return $$"""
                 {"id":"resp_1","object":"response","created_at":1700000000,"status":"completed","model":"{{ModelId}}","output":[{"type":"message","id":"msg_1","status":"completed","role":"assistant","content":[{"type":"output_text","text":"hi there","annotations":[]}]}],"parallel_tool_calls":false,"tools":[],"usage":{{usageJson}}}
                 """;
    }

    private static string BuildStreamingBody(string usageJson)
    {
        const string Created =
            """{"type":"response.created","sequence_number":0,"response":{"id":"resp_1","object":"response","created_at":1700000000,"status":"in_progress","model":"gpt-5.4","output":[],"parallel_tool_calls":false,"tools":[]}}""";
        const string Delta1 = """{"type":"response.output_text.delta","sequence_number":1,"item_id":"msg_1","output_index":0,"content_index":0,"delta":"hi "}""";
        const string Delta2 = """{"type":"response.output_text.delta","sequence_number":2,"item_id":"msg_1","output_index":0,"content_index":0,"delta":"there"}""";
        var completed = """{"type":"response.completed","sequence_number":3,"response":""" + BuildResponseJson(usageJson).Trim() + "}";

        return Sse("response.created", Created)
               + Sse("response.output_text.delta", Delta1)
               + Sse("response.output_text.delta", Delta2)
               + Sse("response.completed", completed);
    }

    private static string Sse(string eventName, string data)
    {
        return $"event: {eventName}\ndata: {data}\n\n";
    }

    private sealed class ReplayHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly string _mediaType;

        public ReplayHandler(string body, string mediaType)
        {
            _body = body;
            _mediaType = mediaType;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, _mediaType)
            });
        }
    }
}
