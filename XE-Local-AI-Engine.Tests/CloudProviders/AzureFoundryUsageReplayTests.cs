namespace XE_Local_AI_Engine.Tests.CloudProviders;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Proves the Azure Foundry v1 Chat Completions client surfaces provider usage, which the runner folds from
///     <see cref="UsageContent" /> on streamed updates, by replaying a stream over a stub transport.
/// </summary>
/// <remarks>
///     Chat Completions streams usage only when the request carries <c>stream_options.include_usage</c>, so the
///     captured request is the real proof; the adapter then reads <c>usage</c> from the final, choice-less chunk.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class AzureFoundryUsageReplayTests
{
    private const string Deployment = "gpt-4o";

    private const string UsageJson =
        """{"prompt_tokens":120,"completion_tokens":45,"total_tokens":165,"completion_tokens_details":{"reasoning_tokens":0}}""";

    [Test]
    public async Task Streaming_RequestsIncludeUsage_AndFinalChunkUsageArrivesAsOneUsageContent()
    {
        using var handler = new ReplayHandler(BuildStreamingBody(UsageJson));
        using var httpClient = new HttpClient(handler);
        var connection = CreateApiKeyConnection();

        // The same chain AzureFoundryChatClientFactory.Create uses for the v1 surface, over a stub transport.
        using var client = new AzureFoundryChatClientFactory()
            .CreateOpenAiV1ClientForTesting(connection, httpClient)
            .GetChatClient(Deployment)
            .AsIChatClient();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            updates.Add(update);
        }

        var requestBody = AssertEx.NotNull(handler.RequestBody, "the chat request must have reached the stub transport");
        using (var json = JsonDocument.Parse(requestBody))
        {
            AssertEx.True(json.RootElement.TryGetProperty("stream_options", out var streamOptions), "request must carry stream_options");
            AssertEx.True(streamOptions.TryGetProperty("include_usage", out var includeUsage), "stream_options must carry include_usage");
            AssertEx.Equal(JsonValueKind.True, includeUsage.ValueKind);
        }

        AssertEx.Equal("hi there", string.Concat(updates.Select(u => u.Text)));
        var usage = updates.SelectMany(u => u.Contents).OfType<UsageContent>().Single();
        AssertEx.Equal(120L, usage.Details.InputTokenCount);
        AssertEx.Equal(45L, usage.Details.OutputTokenCount);
        AssertEx.Equal(165L, usage.Details.TotalTokenCount);
    }

    private static string BuildStreamingBody(string usageJson)
    {
        const string Head = "{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"gpt-4o\"";
        string[] chunks =
        [
            Head + ""","choices":[{"index":0,"delta":{"role":"assistant","content":"hi "},"finish_reason":null}]}""",
            Head + ""","choices":[{"index":0,"delta":{"content":"there"},"finish_reason":null}]}""",
            Head + ""","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            Head + $$""","choices":[],"usage":{{usageJson}}""" + "}"
        ];

        var sb = new StringBuilder();
        foreach (var chunk in chunks)
        {
            sb.Append("data: ").Append(chunk).Append("\n\n");
        }

        return sb.Append("data: [DONE]\n\n").ToString();
    }

    private static StoredAzureFoundryConnection CreateApiKeyConnection()
    {
        return new StoredAzureFoundryConnection
        {
            Endpoint = "https://example.openai.azure.com/",
            AuthMode = AzureFoundryAuthMode.ApiKey,
            ApiSurface = AzureFoundryApiSurface.OpenAiV1,
            ApiKey = "test-key",
            Headers = [],
            Models =
            [
                new StoredAzureFoundryModel
                {
                    DeploymentName = Deployment
                }
            ]
        };
    }

    private sealed class ReplayHandler : HttpMessageHandler
    {
        private readonly string _body;

        public ReplayHandler(string body)
        {
            _body = body;
        }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
