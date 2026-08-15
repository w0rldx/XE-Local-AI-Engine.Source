namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Structured output must actually reach llama.cpp: a teacher run in <c>Constrained</c> mode is only constrained if
///     <c>response_format</c> is on the wire, and llama-server compiles the schema it finds there into GBNF before
///     sampling. Unlike the reasoning switch (see <see cref="DeferredLlamaServerThinkingSwitchTests" />), no injection is
///     needed — <see cref="ChatOptions.ResponseFormat" /> is mapped by the MEAI OpenAI adapter itself, and that adapter's
///     strict-schema transform already rewrites away every repetition keyword
///     <see cref="LlamaGrammarToolSchemaCompatibility" /> guards against on the tools array. These tests assemble the REAL
///     MEAI OpenAI chat pipeline over a request-capturing transport and pin that behaviour, because nothing else in this
///     repository would notice if a package bump dropped either half: the symptom would be an HTTP 400
///     <c>Failed to initialize samplers</c>, or silently unconstrained teacher output, at run time only.
/// </summary>
/// <remarks>
///     Wire shape, read from the pinned llama-server (<c>b10201</c>, <c>tools/server/server-common.cpp</c>, the
///     <c>"Handle \"response_format\" field"</c> block): for <c>type: "json_schema"</c> the server reads the schema from
///     <c>json_schema.schema</c> and ignores the sibling <c>name</c>/<c>description</c>; for <c>type: "json_object"</c> it
///     reads an optional <c>schema</c> and otherwise constrains to free-form JSON. Any other non-empty type but
///     <c>"text"</c> is rejected outright. The adapter emits the nested <c>json_schema</c> form, which is the one the
///     OpenAI API documents and the server's first branch reads.
/// </remarks>
public sealed class DeferredLlamaServerStructuredOutputTests
{
    // A response schema whose bounds all exceed LlamaGrammarToolSchemaCompatibility.MaxGrammarRepetitionBound (1024) —
    // one per keyword that drives GBNF repetition unrolling, at the top level and nested inside an array item.
    private const string OverBoundSchema =
        """
        {"type":"object","properties":{
          "answer":{"type":"string","maxLength":4096,"minLength":2048,"pattern":"^[a-z]{5000}$"},
          "steps":{"type":"array","maxItems":4096,"minItems":2048,"items":{"type":"string","maxLength":9999}}
        },"required":["answer","steps"]}
        """;

    [Test]
    public async Task JsonSchemaResponseFormat_ReachesWireWhereLlamaServerReadsIt()
    {
        using var handler = new CapturingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var chat = BuildOpenAiChatClient(http);

        using var schema = JsonDocument.Parse(OverBoundSchema);
        var options = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement.Clone(), "teacher_sample", "one generated sample")
        };

        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], ApplyClientTransforms(options), CancellationToken.None);

        using var body = JsonDocument.Parse(AssertEx.NotNull(handler.CapturedBody));
        var responseFormat = body.RootElement.GetProperty("response_format");
        AssertEx.Equal("json_schema", responseFormat.GetProperty("type").GetString());

        // The server reads json_schema.schema and nothing else, so the payload must carry the schema at exactly that path.
        var wireSchema = responseFormat.GetProperty("json_schema").GetProperty("schema");
        AssertEx.True(wireSchema.GetProperty("properties").TryGetProperty("answer", out _),
            "the caller's schema must survive to $.response_format.json_schema.schema — that is the only path llama-server reads.");
    }

    [Test]
    public async Task JsonSchemaResponseFormat_CarriesNoBoundGrammarCannotCompile()
    {
        using var handler = new CapturingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var chat = BuildOpenAiChatClient(http);

        using var schema = JsonDocument.Parse(OverBoundSchema);
        var options = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement.Clone(), "teacher_sample")
        };

        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], ApplyClientTransforms(options), CancellationToken.None);

        using var body = JsonDocument.Parse(AssertEx.NotNull(handler.CapturedBody));
        var wireSchema = body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema");

        // Same five keywords LlamaGrammarToolSchemaCompatibility.ExceedsBound screens on the tools array. The adapter's
        // strict-schema transform relocates them into `description` (a hint the model still sees, but not something the
        // GBNF converter unrolls), so an over-bound response schema cannot fail sampler initialisation. Asserting their
        // ABSENCE rather than the transform's mechanism keeps this test true if the adapter starts dropping them outright.
        string[] repetitionKeywords = ["maxLength", "minLength", "maxItems", "minItems", "pattern"];
        foreach (var keyword in repetitionKeywords)
        {
            AssertEx.False(ContainsKeyword(wireSchema, keyword),
                $"'{keyword}' reached the wire schema: llama-server compiles $.response_format.json_schema.schema into "
                + $"GBNF, and a bound above {LlamaGrammarToolSchemaCompatibility.MaxGrammarRepetitionBound} is rejected as "
                + "'number of repetitions exceeds sane defaults' — HTTP 400, the turn never reaches inference.");
        }
    }

    [Test]
    public async Task JsonObjectResponseFormat_ReachesWireAsJsonObject()
    {
        using var handler = new CapturingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var chat = BuildOpenAiChatClient(http);

        // The schema-less variant (ChatResponseFormat.Json). llama-server's json_object branch finds no `schema` and
        // constrains to free-form JSON — weaker than a schema, but a valid and forwarded request, so it is not rejected.
        var options = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };

        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], ApplyClientTransforms(options), CancellationToken.None);

        using var body = JsonDocument.Parse(AssertEx.NotNull(handler.CapturedBody));
        var responseFormat = body.RootElement.GetProperty("response_format");
        AssertEx.Equal("json_object", responseFormat.GetProperty("type").GetString());
        AssertEx.False(responseFormat.TryGetProperty("json_schema", out _),
            "the schema-less variant must not invent a json_schema wrapper.");
    }

    [Test]
    public async Task ResponseFormatAndThinkingSwitch_BothReachWire()
    {
        using var handler = new CapturingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var chat = BuildOpenAiChatClient(http);

        // The combination a Constrained-mode teacher actually runs: a thinking-capable model with reasoning OFF plus a
        // response schema. The two travel by different mechanisms — the switch through
        // ChatOptions.RawRepresentationFactory (a JsonPatch on ChatCompletionOptions), the response format through the
        // adapter's own typed mapping — and both write the same request body, so neither may erase the other.
        using var schema = JsonDocument.Parse(OverBoundSchema);
        var options = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement.Clone(), "teacher_sample"),
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [DeferredLlamaServerChatClient.DisableThinkingMarkerKey] = true
            }
        };

        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], ApplyClientTransforms(options), CancellationToken.None);

        using var body = JsonDocument.Parse(AssertEx.NotNull(handler.CapturedBody));
        AssertEx.Equal(expected: false, body.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        AssertEx.Equal("json_schema", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Test]
    public async Task NoResponseFormat_BodyHasNoResponseFormat()
    {
        using var handler = new CapturingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var chat = BuildOpenAiChatClient(http);

        // Negative control: without a ResponseFormat the client's transforms return the options unchanged and no
        // constraint is invented, so every non-structured request stays byte-identical to what it sends today.
        var options = new ChatOptions();
        var passthrough = ApplyClientTransforms(options);
        AssertEx.True(ReferenceEquals(options, passthrough), "with no marker and no tools the options must be returned unchanged.");

        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], passthrough, CancellationToken.None);

        var body = AssertEx.NotNull(handler.CapturedBody);
        AssertEx.False(body.Contains("response_format", StringComparison.Ordinal),
            "a request with no ResponseFormat must never carry response_format.");
    }

    // The exact options pipeline DeferredLlamaServerChatClient runs before handing options to its inner adapter. Both
    // transforms clone the options when they act, so this is what proves the response format survives that clone.
    private static ChatOptions? ApplyClientTransforms(ChatOptions? options) =>
        DeferredLlamaServerChatClient.ApplyToolSchemaCompatibility(DeferredLlamaServerChatClient.ApplyThinkingSwitch(options));

    private static bool ContainsKeyword(JsonElement element, string keyword)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals(keyword) || ContainsKeyword(property.Value, keyword))
                    {
                        return true;
                    }
                }

                return false;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ContainsKeyword(item, keyword))
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    private static IChatClient BuildOpenAiChatClient(HttpClient http)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri("http://127.0.0.1:1/v1"),
            Transport = new HttpClientPipelineTransport(http),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0)
        };
        var client = new OpenAIClient(new ApiKeyCredential("ignored"), options);
        return client.GetChatClient("test-model").AsIChatClient();
    }

    /// <summary>Captures the outbound request body and returns a canned OpenAI chat completion so no network is hit.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private const string CannedCompletion =
            "{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":0,\"model\":\"test-model\","
            + "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}],"
            + "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

        public string? CapturedBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                CapturedBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CannedCompletion, Encoding.UTF8, "application/json")
            };
        }
    }
}
