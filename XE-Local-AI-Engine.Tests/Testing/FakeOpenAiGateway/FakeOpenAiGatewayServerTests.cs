namespace XE_Local_AI_Engine.Tests.Testing.FakeOpenAiGateway;

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Testing.FakeOpenAiGateway;

/// <summary>
///     The fake OpenAI-compatible gateway's own wire contract: auth and header gates, the completion and SSE shapes,
///     the tool-call script, each fault, the request log and the control endpoints. Every later gateway test rests on it.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class FakeOpenAiGatewayServerTests
{
    private const string Model = "gateway-demo-model";
    private const string Token = "demo-token";
    private const string TenantHeader = "X-Demo-Tenant";
    private const string TenantValue = "demo-tenant";

    [Test]
    public async Task BaseAddress_IsLoopbackAndEndsWithTheBasePath()
    {
        await using var gateway = await FakeOpenAiGatewayServer.StartAsync();

        AssertEx.Equal("127.0.0.1", gateway.BaseAddress.Host);
        AssertEx.Equal("/v1/", gateway.BaseAddress.AbsolutePath);
    }

    [Test]
    public async Task Models_ListsEachModelWithContextAndOutputExtras()
    {
        await using var gateway = await FakeOpenAiGatewayServer.StartAsync(new FakeOpenAiGatewayOptions
        {
            Models =
            [
                new FakeOpenAiGatewayModel
                {
                    Id = "alpha",
                    ContextLength = 8192
                },
                new FakeOpenAiGatewayModel
                {
                    Id = "beta",
                    ContextLength = 131072,
                    MaxOutputTokens = 16384,
                    OwnedBy = "demo",
                    Metadata = new Dictionary<string, string>
                    {
                        ["family"] = "demo"
                    }
                }
            ]
        });
        using var client = Client(gateway);

        using var response = await client.GetAsync(new Uri("models", UriKind.Relative));
        using var json = await ReadJsonAsync(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("list", json.RootElement.GetProperty("object").GetString());
        var data = json.RootElement.GetProperty("data").EnumerateArray().ToArray();
        AssertEx.Equal(expected: 2, data.Length);
        AssertEx.Equal("alpha", data[0].GetProperty("id").GetString());
        AssertEx.Equal("model", data[0].GetProperty("object").GetString());
        AssertEx.Equal(expected: 8192, data[0].GetProperty("context_length").GetInt32());
        AssertEx.Equal(expected: 4096, data[0].GetProperty("max_output_tokens").GetInt32());
        AssertEx.False(data[0].TryGetProperty("metadata", out _), "A model without metadata must not carry the field.");
        AssertEx.Equal(expected: 131072, data[1].GetProperty("context_length").GetInt32());
        AssertEx.Equal(expected: 16384, data[1].GetProperty("max_output_tokens").GetInt32());
        AssertEx.Equal("demo", data[1].GetProperty("owned_by").GetString());
        AssertEx.Equal("demo", data[1].GetProperty("metadata").GetProperty("family").GetString());
    }

    [Test]
    [Arguments(null)]
    [Arguments("wrong-token")]
    public async Task Request_WithoutTheRequiredBearer_Answers401InOpenAiShape(string? bearer)
    {
        await using var gateway = await FakeOpenAiGatewayServer.StartAsync(new FakeOpenAiGatewayOptions
        {
            RequiredBearerToken = Token
        });
        using var client = Client(gateway, bearer);

        using var response = await client.GetAsync(new Uri("models", UriKind.Relative));
        using var json = await ReadJsonAsync(response);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertEx.Equal("invalid_api_key", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        AssertEx.Equal("invalid_request_error", json.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Test]
    [Arguments(null)]
    [Arguments("other-tenant")]
    public async Task Request_WithoutTheRequiredHeader_Answers403NamingIt(string? headerValue)
    {
        await using var gateway = await FakeOpenAiGatewayServer.StartAsync(GatedOptions());
        using var client = Client(gateway, Token);
        if (headerValue is not null)
        {
            client.DefaultRequestHeaders.Add(TenantHeader, headerValue);
        }

        using var response = await client.GetAsync(new Uri("models", UriKind.Relative));
        using var json = await ReadJsonAsync(response);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var message = json.RootElement.GetProperty("error").GetProperty("message").GetString();
        AssertEx.Contains(message, TenantHeader);
        AssertEx.False(message!.Contains(TenantValue, StringComparison.Ordinal), "The 403 body must name the header, never echo a value.");
    }

    [Test]
    public async Task Request_WithBearerAndHeader_PassesBothGates()
    {
        await using var gateway = await FakeOpenAiGatewayServer.StartAsync(GatedOptions());
        using var client = GatedClient(gateway);

        using var response = await client.GetAsync(new Uri("models", UriKind.Relative));

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Test]
    public async Task Bearer_IsCheckedBeforeTheFaultQueue_SoARejectedRequestLeavesTheFaultQueued()
    {
        await using var gateway = await FakeOpenAiGatewayServer.StartAsync(GatedOptions());
        gateway.State.EnqueueFailure(FakeOpenAiGatewayFailure.Http500);
        using var anonymous = Client(gateway);
        using var authorized = GatedClient(gateway);

        using var rejected = await anonymous.GetAsync(new Uri("models", UriKind.Relative));
        using var faulted = await authorized.GetAsync(new Uri("models", UriKind.Relative));

        AssertEx.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        AssertEx.Equal(HttpStatusCode.InternalServerError, faulted.StatusCode);
    }

    [Test]
    public async Task NonStreamCompletion_ReturnsAChatCompletionWithUsage()
    {
        await using var gateway = await FakeOpenAiGatewayServer.StartAsync(new FakeOpenAiGatewayOptions
        {
            DefaultCompletionText = "one two three"
        });
        using var client = Client(gateway);

        using var response = await PostChatAsync(client, stream: false);
        using var json = await ReadJsonAsync(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = json.RootElement;
        AssertEx.Equal("chat.completion", root.GetProperty("object").GetString());
        AssertEx.Equal(Model, root.GetProperty("model").GetString());
        var choice = root.GetProperty("choices")[0];
        AssertEx.Equal("assistant", choice.GetProperty("message").GetProperty("role").GetString());
        AssertEx.Equal("one two three", choice.GetProperty("message").GetProperty("content").GetString());
        AssertEx.Equal("stop", choice.GetProperty("finish_reason").GetString());
        var usage = root.GetProperty("usage");
        AssertEx.Equal(expected: 3, usage.GetProperty("completion_tokens").GetInt32());
        AssertEx.Equal(usage.GetProperty("prompt_tokens").GetInt32() + 3, usage.GetProperty("total_tokens").GetInt32());
    }

    [Test]
    public async Task StreamCompletion_FramesChunksThenAUsageOnlyChunkThenDone()
    {
        await using var gateway = await FakeOpenAiGatewayServer.StartAsync(new FakeOpenAiGatewayOptions
        {
            DefaultCompletionText = "one two three"
        });
        using var client = Client(gateway);

        using var response = await PostChatAsync(client, stream: true);
        var events = await ReadEventsAsync(response);

        AssertEx.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        AssertEx.Equal("[DONE]", events[^1]);
        var chunks = events[..^1].Select(data => JsonDocument.Parse(data).RootElement).ToArray();
        AssertEx.True(chunks.All(chunk => chunk.GetProperty("object").GetString() == "chat.completion.chunk"), "Every frame before [DONE] must be a chunk.");

        var usageChunk = chunks[^1];
        AssertEx.Equal(expected: 0, usageChunk.GetProperty("choices").GetArrayLength());
        AssertEx.Equal(expected: 3, usageChunk.GetProperty("usage").GetProperty("completion_tokens").GetInt32());
        AssertEx.Equal("stop", chunks[^2].GetProperty("choices")[0].GetProperty("finish_reason").GetString());

        var text = string.Concat(chunks[..^1].Select(chunk => chunk.GetProperty("choices")[0].GetProperty("delta"))
                                             .Where(delta => delta.TryGetProperty("content", out _))
                                             .Select(delta => delta.GetProperty("content").GetString()));
        AssertEx.Equal("one two three", text);
    }

    [Test]
    public async Task ToolCallScript_NonStream_EmitsToolCalls()
    {
        await using var gateway = await FakeOpenAiGatewayServer.StartAsync();
        gateway.State.SetScript(new FakeOpenAiGatewayScript
        {
            ToolCall = new FakeOpenAiGatewayToolCall
            {
                Name = "calculator",
                Arguments = """{"expression":"12*9"}"""
            }
        });
        using var client = Client(gateway);

        using var response = await PostChatAsync(client, stream: false);
        using var json = await ReadJsonAsync(response);

        var choice = json.RootElement.GetProperty("choices")[0];
        AssertEx.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());
        var call = choice.GetProperty("message").GetProperty("tool_calls")[0];
        AssertEx.Equal("function", call.GetProperty("type").GetString());
        AssertEx.Equal("calculator", call.GetProperty("function").GetProperty("name").GetString());
        AssertEx.Equal("""{"expression":"12*9"}""", call.GetProperty("function").GetProperty("arguments").GetString());
    }

    [Test]
    public async Task ToolCallScript_Stream_EmitsToolCallDeltasThatAccumulateToTheArguments()
    {
        await using var gateway = await FakeOpenAiGatewayServer.StartAsync();
        gateway.State.SetScript(new FakeOpenAiGatewayScript
        {
            ToolCall = new FakeOpenAiGatewayToolCall
            {
                Name = "calculator",
                Arguments = """{"expression":"12*9"}"""
            }
        });
        using var client = Client(gateway);

        using var response = await PostChatAsync(client, stream: true);
        var events = await ReadEventsAsync(response);

        AssertEx.Equal("[DONE]", events[^1]);
        var deltas = events[..^1].Select(data => JsonDocument.Parse(data).RootElement)
                                 .Where(chunk => chunk.GetProperty("choices").GetArrayLength() > 0)
                                 .Select(chunk => chunk.GetProperty("choices")[0])
                                 .ToArray();
        var calls = deltas.Where(choice => choice.GetProperty("delta").TryGetProperty("tool_calls", out _))
                          .Select(choice => choice.GetProperty("delta").GetProperty("tool_calls")[0].GetProperty("function"))
                          .ToArray();
        AssertEx.True(calls.Length > 1, "The arguments must arrive over more than one delta.");
        AssertEx.Equal("calculator", calls[0].GetProperty("name").GetString());
        AssertEx.Equal("""{"expression":"12*9"}""", string.Concat(calls.Select(function => function.GetProperty("arguments").GetString())));
        AssertEx.Equal("tool_calls", deltas[^1].GetProperty("finish_reason").GetString());
    }

    [Test]
    public async Task Script_AnswersOnlyTheNextCountRequests()
    {
        await using var gateway = await FakeOpenAiGatewayServer.StartAsync(new FakeOpenAiGatewayOptions
        {
            DefaultCompletionText = "default"
        });
        using var client = Client(gateway);
        using var scripted = await PostJsonAsync(client, new Uri(gateway.BaseAddress, "/test/script"), """{"completionText":"scripted","count":2}""");
        AssertEx.Equal(HttpStatusCode.NoContent, scripted.StatusCode);

        var replies = new List<string?>();
        for (var request = 0; request < 3; request++)
        {
            using var response = await PostChatAsync(client, stream: false);
            using var json = await ReadJsonAsync(response);
            replies.Add(json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        }

        AssertEx.Equal("scripted,scripted,default", string.Join(',', replies));
    }

    [Test]
    public async Task UnknownModel_Answers404InOpenAiShape()
    {
        await using var gateway = await FakeOpenAiGatewayServer.StartAsync();
        using var client = Client(gateway);

        using var response = await PostChatAsync(client, stream: false, model: "no-such-model");
        using var json = await ReadJsonAsync(response);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertEx.Equal("model_not_found", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Test]
    [Arguments(FakeOpenAiGatewayFailure.Unauthorized, HttpStatusCode.Unauthorized, "invalid_api_key")]
    [Arguments(FakeOpenAiGatewayFailure.Forbidden, HttpStatusCode.Forbidden, "forbidden")]
    [Arguments(FakeOpenAiGatewayFailure.UnknownModel, HttpStatusCode.NotFound, "model_not_found")]
    [Arguments(FakeOpenAiGatewayFailure.RateLimited, HttpStatusCode.TooManyRequests, "rate_limit_exceeded")]
    [Arguments(FakeOpenAiGatewayFailure.Http500, HttpStatusCode.InternalServerError, null)]
    public async Task StatusFault_AnswersOnceInOpenAiShape_ThenTheNextRequestSucceeds(FakeOpenAiGatewayFailure failure, HttpStatusCode status, string? code)
    {
        await using var gateway = await FakeOpenAiGatewayServer.StartAsync();
        gateway.State.EnqueueFailure(failure);
        using var client = Client(gateway);

        using var faulted = await PostChatAsync(client, stream: false);
        using var json = await ReadJsonAsync(faulted);
        using var next = await PostChatAsync(client, stream: false);

        AssertEx.Equal(status, faulted.StatusCode);
        var error = json.RootElement.GetProperty("error");
        AssertEx.Equal<string?>(code, error.GetProperty("code").GetString());
        AssertEx.NotNullOrEmpty(error.GetProperty("message").GetString());
        AssertEx.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Test]
    public async Task RateLimitedFault_CarriesRetryAfter()
    {
        await using var gateway = await FakeOpenAiGatewayServer.StartAsync();
        gateway.State.EnqueueFailure(FakeOpenAiGatewayFailure.RateLimited);
        using var client = Client(gateway);

        using var response = await client.GetAsync(new Uri("models", UriKind.Relative));

        AssertEx.Equal(TimeSpan.FromSeconds(7), response.Headers.RetryAfter?.Delta);
    }

    [Test]
    public async Task MalformedJsonFault_AnswersOkWithABodyThatDoesNotParse()
    {
        await using var gateway = await FakeOpenAiGatewayServer.StartAsync();
        gateway.State.EnqueueFailure(FakeOpenAiGatewayFailure.MalformedJson);
        using var client = Client(gateway);

        using var response = await PostChatAsync(client, stream: false);
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Throws<JsonException>(() => JsonDocument.Parse(body));
    }

    [Test]
    public async Task PartialStreamFault_SendsChunksThenClosesWithoutDone()
    {
        await using var gateway = await FakeOpenAiGatewayServer.StartAsync();
        gateway.State.EnqueueFailure(FakeOpenAiGatewayFailure.PartialStream);
        using var client = Client(gateway);

        using var response = await PostChatAsync(client, stream: true);
        var events = await ReadEventsAsync(response);

        AssertEx.True(response.Headers.ConnectionClose == true, "The partial stream must close its connection.");
        AssertEx.Equal(expected: 2, events.Length);
        AssertEx.False(events.Contains("[DONE]"), "A partial stream must never send [DONE].");
        AssertEx.True(events.All(data => !data.Contains("finish_reason\":\"", StringComparison.Ordinal) && !data.Contains("usage", StringComparison.Ordinal)),
            "A partial stream must stop before the finish and usage chunks.");
    }

    [Test]
    [SuppressMessage("Reliability", "CA2025:Ensure tasks using IDisposable instances complete before disposal",
        Justification = "The pending request is awaited before the client and token source leave scope.")]
    public async Task HangFault_NeverAnswersUntilTheCallerCancels()
    {
        await using var gateway = await FakeOpenAiGatewayServer.StartAsync();
        gateway.State.EnqueueFailure(FakeOpenAiGatewayFailure.Hang);
        using var client = Client(gateway);
        using var cancellation = new CancellationTokenSource();

        var pending = PostChatAsync(client, stream: false, cancellationToken: cancellation.Token);
        await AssertEx.EventuallyAsync(() => gateway.RecordedRequests.Count == 1, TestBudgets.Contended, "The hanging request never reached the fake.");
        await AssertEx.StaysIncompleteAsync(pending, "The hang fault answered.");
        await cancellation.CancelAsync();

        await AssertEx.ThrowsAsync<TaskCanceledException>(async () => await pending);
    }

    [Test]
    public async Task RecordedRequests_CarryEveryHeaderVerbatimAndTheRequestShape()
    {
        await using var gateway = await FakeOpenAiGatewayServer.StartAsync(GatedOptions());
        using var client = GatedClient(gateway);

        using var response = await PostChatAsync(client, stream: true, toolCount: 2);
        await response.Content.ReadAsStringAsync();

        var recorded = gateway.RecordedRequests.Single();
        AssertEx.Equal("POST", recorded.Method);
        AssertEx.Equal("/v1/chat/completions", recorded.Path);
        AssertEx.Equal(Model, recorded.Model);
        AssertEx.True(recorded.Stream, "The stream flag was not recorded.");
        AssertEx.Equal(expected: 1, recorded.MessageCount);
        AssertEx.Equal(expected: 2, recorded.ToolCount);
        AssertEx.Equal("Bearer " + Token, recorded.Headers["authorization"]);
        AssertEx.Equal(TenantValue, recorded.Headers[TenantHeader]);
    }

    [Test]
    public async Task ControlEndpoints_ExposeAndClearTheRequestLog()
    {
        await using var gateway = await FakeOpenAiGatewayServer.StartAsync(GatedOptions());
        using var client = GatedClient(gateway);
        using var models = await client.GetAsync(new Uri("models", UriKind.Relative));

        using var listed = await client.GetAsync(new Uri(gateway.BaseAddress, "/test/requests"));
        using var json = await ReadJsonAsync(listed);
        using var cleared = await client.DeleteAsync(new Uri(gateway.BaseAddress, "/test/requests"));

        var entry = json.RootElement.EnumerateArray().Single();
        AssertEx.Equal("/v1/models", entry.GetProperty("path").GetString());
        AssertEx.Equal("Bearer " + Token, entry.GetProperty("headers").GetProperty("Authorization").GetString());
        AssertEx.Equal(TenantValue, entry.GetProperty("headers").GetProperty(TenantHeader).GetString());
        AssertEx.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        AssertEx.Empty(gateway.RecordedRequests);
    }

    [Test]
    public async Task ControlEndpoints_QueueAndClearFaults()
    {
        await using var gateway = await FakeOpenAiGatewayServer.StartAsync();
        using var client = Client(gateway);

        var failures = new Uri(gateway.BaseAddress, "/test/failures");

        using var queued = await PostJsonAsync(client, failures, """{"failure":"rateLimited"}""");
        using var limited = await client.GetAsync(new Uri("models", UriKind.Relative));
        using var queuedAgain = await PostJsonAsync(client, failures, """{"failure":"Http500"}""");
        using var cleared = await client.DeleteAsync(failures);
        using var healthy = await client.GetAsync(new Uri("models", UriKind.Relative));
        using var invalid = await PostJsonAsync(client, failures, """{"failure":"NoSuchFault"}""");

        AssertEx.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        AssertEx.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        AssertEx.Equal(HttpStatusCode.Accepted, queuedAgain.StatusCode);
        AssertEx.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        AssertEx.Equal(HttpStatusCode.OK, healthy.StatusCode);
        AssertEx.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Test]
    public async Task ControlEndpoints_WithAToken_RejectACallerWithoutIt()
    {
        await using var gateway = await FakeOpenAiGatewayServer.StartAsync(new FakeOpenAiGatewayOptions
        {
            ControlEndpointToken = "control-demo"
        });
        using var client = Client(gateway);
        var requests = new Uri(gateway.BaseAddress, "/test/requests");

        using var missing = await client.GetAsync(requests);
        using var wrong = new HttpRequestMessage(HttpMethod.Get, requests);
        wrong.Headers.Add("X-Test-Sink-Token", "not-it");
        using var wrongResponse = await client.SendAsync(wrong);
        using var right = new HttpRequestMessage(HttpMethod.Get, requests);
        right.Headers.Add("X-Test-Sink-Token", "control-demo");
        using var rightResponse = await client.SendAsync(right);

        AssertEx.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        AssertEx.Equal(HttpStatusCode.Unauthorized, wrongResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.OK, rightResponse.StatusCode);
    }

    private static FakeOpenAiGatewayOptions GatedOptions()
    {
        return new FakeOpenAiGatewayOptions
        {
            RequiredBearerToken = Token,
            RequiredHeaders = new Dictionary<string, string>
            {
                [TenantHeader] = TenantValue
            }
        };
    }

    private static HttpClient GatedClient(FakeOpenAiGatewayServer gateway)
    {
        var client = Client(gateway, Token);
        client.DefaultRequestHeaders.Add(TenantHeader, TenantValue);
        return client;
    }

    private static HttpClient Client(FakeOpenAiGatewayServer gateway, string? bearer = null)
    {
        var client = new HttpClient
        {
            BaseAddress = gateway.BaseAddress
        };
        if (bearer is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return client;
    }

    private static async Task<HttpResponseMessage> PostChatAsync(HttpClient client, bool stream, string model = Model, int toolCount = 0,
        CancellationToken cancellationToken = default)
    {
        using var content = ChatBody(stream, model, toolCount);
        return await client.PostAsync(new Uri("chat/completions", UriKind.Relative), content, cancellationToken);
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, Uri uri, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.PostAsync(uri, content);
    }

    private static StringContent ChatBody(bool stream, string model = Model, int toolCount = 0)
    {
        var tools = string.Join(',', Enumerable.Range(0, toolCount).Select(index =>
            $$$$"""{"type":"function","function":{"name":"tool{{{{index}}}}","parameters":{"type":"object"}}}"""));
        var json = $$"""{"model":"{{model}}","stream":{{(stream ? "true" : "false")}},"messages":[{"role":"user","content":"hello"}],"tools":[{{tools}}]}""";
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string[]> ReadEventsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                   .Select(frame => frame.StartsWith("data: ", StringComparison.Ordinal) ? frame["data: ".Length..] : throw new InvalidOperationException($"Not an SSE data frame: {frame}"))
                   .ToArray();
    }
}
