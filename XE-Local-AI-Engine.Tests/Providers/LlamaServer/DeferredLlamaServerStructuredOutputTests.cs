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
///     Structured output must reach llama.cpp AS THE AUTHOR WROTE IT. A teacher run in <c>Constrained</c> mode is only
///     constrained if <c>response_format</c> is on the wire, and llama-server compiles whatever it finds at
///     <c>$.response_format.json_schema.schema</c> into a real GBNF grammar before sampling — so every keyword that
///     survives that path is enforced and every keyword that does not is gone.
///     <para>
///         Left to itself the <c>Microsoft.Extensions.AI.OpenAI</c> adapter does not leave it alone: its strict-schema
///         transform (<c>OpenAIClientExtensions.StrictSchemaTransformCache</c>) runs unconditionally, with no opt-out in
///         10.9.0 or on main, relocating twenty-two value keywords into the schema's <c>description</c> prose, marking
///         every declared property <c>required</c> and closing objects with <c>additionalProperties: false</c>. Right for
///         the OpenAI API, wrong for llama.cpp. <see cref="DeferredLlamaServerChatClient.ApplyResponseSchemaPassthrough" />
///         therefore writes the authored schema onto the request body itself, which the adapter fills in only with
///         <c>??=</c> and so leaves alone.
///     </para>
///     <para>
///         These tests assemble the REAL MEAI OpenAI chat pipeline over a request-capturing transport and grade the
///         serialized bytes, because nothing else in this repository would notice if a package bump moved this: the
///         symptom would be an unenforced bound, or an HTTP 400 <c>Failed to initialize samplers</c>, at run time only.
///     </para>
/// </summary>
/// <remarks>
///     Wire shape, read from the pinned llama-server (<c>b10201</c>, <c>tools/server/server-common.cpp</c>, the
///     <c>"Handle \"response_format\" field"</c> block): for <c>type: "json_schema"</c> the server reads the schema from
///     <c>json_schema.schema</c> and ignores the sibling <c>name</c>/<c>description</c>/<c>strict</c>; for
///     <c>type: "json_object"</c> it reads an optional <c>schema</c> and otherwise constrains to free-form JSON. Any
///     other non-empty type but <c>"text"</c> is rejected outright.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class DeferredLlamaServerStructuredOutputTests
{
    // The schema an author actually writes: tight value bounds, all of them well inside
    // LlamaGrammarToolSchemaCompatibility.MaxGrammarRepetitionBound, and one property (`notes`) deliberately left out
    // of `required`. Every one of these is something the strict transform used to destroy.
    private const string AuthoredSchema =
        """
        {"type":"object","properties":{
          "answer":{"type":"string","minLength":2,"maxLength":3,"pattern":"^[a-z]{2,3}$"},
          "score":{"type":"integer","minimum":1,"maximum":10},
          "notes":{"type":"string"}
        },"required":["answer","score"]}
        """;

    // The same shape with bounds llama.cpp's GBNF converter cannot unroll (4096, and a `{5000}` quantifier) sitting
    // next to bounds it can (8, 2, 64). Only the first group may be removed.
    private const string OverBoundSchema =
        """
        {"type":"object","properties":{
          "answer":{"type":"string","maxLength":4096,"minLength":8,"pattern":"^[a-z]{5000}$"},
          "steps":{"type":"array","maxItems":4096,"minItems":2,"items":{"type":"string","maxLength":64}}
        },"required":["answer","steps"]}
        """;

    [Test]
    public async Task JsonSchemaResponseFormat_ReachesWireWhereLlamaServerReadsIt()
    {
        var body = await CaptureAsync(SchemaOptions(AuthoredSchema, "teacher_sample", "one generated sample"));

        using var document = JsonDocument.Parse(body);
        var responseFormat = document.RootElement.GetProperty("response_format");
        AssertEx.Equal("json_schema", responseFormat.GetProperty("type").GetString());

        // The server reads json_schema.schema and nothing else, so the payload must carry the schema at exactly that path.
        var wireSchema = responseFormat.GetProperty("json_schema").GetProperty("schema");
        AssertEx.True(wireSchema.GetProperty("properties").TryGetProperty("answer", out _),
            "the caller's schema must survive to $.response_format.json_schema.schema — that is the only path llama-server reads.");
    }

    /// <summary>
    ///     The whole point of the passthrough: the bounds the transform used to relocate arrive intact, the optional
    ///     property is still optional, and nothing was closed or annotated on the way out.
    /// </summary>
    [Test]
    public async Task JsonSchemaResponseFormat_CarriesTheAuthoredKeywordsVerbatim()
    {
        // Baseline: the SAME options WITHOUT the passthrough lose every one of these. This is the defect being fixed,
        // so it also proves the assertions below are load-bearing rather than asserting what the adapter already did.
        var untransformed = await CaptureAsync(SchemaOptions(AuthoredSchema, "teacher_sample"), applyClientTransforms: false);
        using (var baseline = JsonDocument.Parse(untransformed))
        {
            var baselineAnswer = baseline.RootElement.GetProperty("response_format")
                                         .GetProperty("json_schema")
                                         .GetProperty("schema")
                                         .GetProperty("properties")
                                         .GetProperty("answer");
            AssertEx.False(baselineAnswer.TryGetProperty("maxLength", out _),
                "the MEAI strict transform is expected to strip maxLength; if it stops, the passthrough is redundant and this pin should be revisited.");
            AssertEx.Contains(baselineAnswer.GetProperty("description").GetString() ?? string.Empty,
                "maxLength: 3",
                message: "the baseline defect is the bound becoming description PROSE — that is what must no longer happen.");
        }

        var body = await CaptureAsync(SchemaOptions(AuthoredSchema, "teacher_sample"));

        using var document = JsonDocument.Parse(body);
        var schema = document.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema");
        var answer = schema.GetProperty("properties").GetProperty("answer");

        AssertEx.Equal(expected: 2, answer.GetProperty("minLength").GetInt32());
        AssertEx.Equal(expected: 3, answer.GetProperty("maxLength").GetInt32());
        AssertEx.Equal("^[a-z]{2,3}$", answer.GetProperty("pattern").GetString());
        AssertEx.False(answer.TryGetProperty("description", out _),
            "no keyword may be relocated into description prose: llama-server enforces the keyword and ignores the prose.");

        var score = schema.GetProperty("properties").GetProperty("score");
        AssertEx.Equal(expected: 1, score.GetProperty("minimum").GetInt32());
        AssertEx.Equal(expected: 10, score.GetProperty("maximum").GetInt32());

        AssertEx.Equal("answer,score",
            string.Join(',', schema.GetProperty("required").EnumerateArray().Select(static entry => entry.GetString())),
            "'notes' was left optional by the author and must stay optional — the transform used to make every property required.");
        AssertEx.False(ContainsKeyword(schema, "additionalProperties"),
            "the author closed nothing, so nothing may be closed for them: the transform used to inject additionalProperties: false.");
    }

    /// <summary>
    ///     The one thing the passthrough must still take away. llama.cpp compiles this schema into GBNF and refuses a
    ///     repetition bound above <see cref="LlamaGrammarToolSchemaCompatibility.MaxGrammarRepetitionBound" /> outright —
    ///     HTTP 400 <c>Failed to initialize samplers</c>, the turn never reaches inference. The strict transform used to
    ///     hide that by accident; the passthrough now does it on purpose, and ONLY to the offending keywords.
    /// </summary>
    [Test]
    public async Task JsonSchemaResponseFormat_CarriesNoBoundGrammarCannotCompile()
    {
        var body = await CaptureAsync(SchemaOptions(OverBoundSchema, "teacher_sample"));

        using var document = JsonDocument.Parse(body);
        var schema = document.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema");
        var answer = schema.GetProperty("properties").GetProperty("answer");
        var steps = schema.GetProperty("properties").GetProperty("steps");

        AssertEx.False(answer.TryGetProperty("maxLength", out _), $"maxLength 4096 is above {LlamaGrammarToolSchemaCompatibility.MaxGrammarRepetitionBound} and must be dropped.");
        AssertEx.False(answer.TryGetProperty("pattern", out _), "a pattern whose {5000} quantifier unrolls past the cap must be dropped whole.");
        AssertEx.False(steps.TryGetProperty("maxItems", out _), "maxItems 4096 must be dropped.");

        AssertEx.Equal(expected: 8, answer.GetProperty("minLength").GetInt32(), "an in-range bound on the SAME property must survive.");
        AssertEx.Equal(expected: 2, steps.GetProperty("minItems").GetInt32(), "an in-range bound must survive alongside a dropped sibling.");
        AssertEx.Equal(expected: 64, steps.GetProperty("items").GetProperty("maxLength").GetInt32(), "an in-range bound nested under items must survive.");
    }

    /// <summary>
    ///     <c>strict</c> is an OpenAI structured-output guarantee. llama-server ignores the field, and llama.cpp makes no
    ///     such promise, so claiming it on the wire would be a lie nobody checks.
    /// </summary>
    [Test]
    public async Task JsonSchemaResponseFormat_DoesNotClaimOpenAiStrictness()
    {
        var body = await CaptureAsync(SchemaOptions(AuthoredSchema, "teacher_sample"));

        using var document = JsonDocument.Parse(body);
        var jsonSchema = document.RootElement.GetProperty("response_format").GetProperty("json_schema");
        AssertEx.Equal(expected: false, jsonSchema.GetProperty("strict").GetBoolean());
    }

    [Test]
    public async Task JsonObjectResponseFormat_ReachesWireAsJsonObject()
    {
        // The schema-less variant (ChatResponseFormat.Json). The passthrough has no schema to write, so this is the
        // adapter's own mapping: llama-server's json_object branch finds no `schema` and constrains to free-form JSON.
        var body = await CaptureAsync(new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.Json
        });

        using var document = JsonDocument.Parse(body);
        var responseFormat = document.RootElement.GetProperty("response_format");
        AssertEx.Equal("json_object", responseFormat.GetProperty("type").GetString());
        AssertEx.False(responseFormat.TryGetProperty("json_schema", out _),
            "the schema-less variant must not invent a json_schema wrapper.");
    }

    /// <summary>
    ///     The combination a Constrained-mode teacher actually runs: a thinking-capable model with reasoning OFF plus a
    ///     response schema. Both now ride <c>ChatOptions.RawRepresentationFactory</c>, which is a single slot — so this
    ///     is the test that fails if either transform assigns a factory instead of chaining the one already there.
    /// </summary>
    [Test]
    public async Task ResponseFormatAndThinkingSwitch_BothReachWire()
    {
        var options = SchemaOptions(AuthoredSchema, "teacher_sample");
        options.AdditionalProperties = new AdditionalPropertiesDictionary
        {
            [DeferredLlamaServerChatClient.DisableThinkingMarkerKey] = true
        };

        var body = await CaptureAsync(options);

        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(expected: false, document.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        AssertEx.Equal("json_schema", document.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        AssertEx.Equal(expected: 3,
            document.RootElement.GetProperty("response_format")
                    .GetProperty("json_schema")
                    .GetProperty("schema")
                    .GetProperty("properties")
                    .GetProperty("answer")
                    .GetProperty("maxLength")
                    .GetInt32(),
            "the raw schema must survive the switch's own patch — one factory slot, two patches.");
    }

    [Test]
    public async Task NoResponseFormat_BodyHasNoResponseFormat()
    {
        // Negative control: without a ResponseFormat the client's transforms return the options unchanged and no
        // constraint is invented, so every non-structured request stays byte-identical to what it sends today.
        var options = new ChatOptions();
        var passthrough = ApplyClientTransforms(options);
        AssertEx.True(ReferenceEquals(options, passthrough), "with no marker, no tools and no schema the options must be returned unchanged.");

        var body = await CaptureAsync(options);

        AssertEx.False(body.Contains("response_format", StringComparison.Ordinal),
            "a request with no ResponseFormat must never carry response_format.");
    }

    private static ChatOptions SchemaOptions(string schemaJson, string name, string? description = null)
    {
        using var schema = JsonDocument.Parse(schemaJson);
        return new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement.Clone(), name, description)
        };
    }

    private static async Task<string> CaptureAsync(ChatOptions? options, bool applyClientTransforms = true)
    {
        using var handler = new CapturingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var chat = BuildOpenAiChatClient(http);

        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")],
            applyClientTransforms ? ApplyClientTransforms(options) : options,
            CancellationToken.None);

        return AssertEx.NotNull(handler.CapturedBody);
    }

    // The exact options pipeline DeferredLlamaServerChatClient runs before handing options to its inner adapter. Every
    // transform that acts clones the options, so running the whole chain here is what proves they compose.
    private static ChatOptions? ApplyClientTransforms(ChatOptions? options) =>
        DeferredLlamaServerChatClient.ApplyToolSchemaCompatibility(DeferredLlamaServerChatClient.ApplyResponseSchemaPassthrough(
            DeferredLlamaServerChatClient.ApplySamplingPassthrough(DeferredLlamaServerChatClient.ApplyReasoningBudget(DeferredLlamaServerChatClient.ApplyThinkingSwitch(options)))));

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
                CapturedBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CannedCompletion, Encoding.UTF8, "application/json")
            };
        }
    }
}
