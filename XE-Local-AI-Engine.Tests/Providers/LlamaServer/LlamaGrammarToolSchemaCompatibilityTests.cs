namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenAI;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     llama-server compiles the whole offered <c>tools</c> array into ONE constrained GBNF grammar before sampling, and
///     rejects a repetition bound that is too large with
///     <c>parse: error parsing grammar: number of repetitions exceeds sane defaults</c> — surfaced as HTTP 400
///     <c>Failed to initialize samplers: failed to parse grammar</c>, i.e. the turn dies before inference. These tests
///     pin the llama.cpp-scoped compatibility pass that removes exactly those bounds on the wire:
///     <see cref="LlamaGrammarToolSchemaCompatibility" /> and
///     <see cref="DeferredLlamaServerChatClient.ApplyToolSchemaCompatibility" />.
/// </summary>
public sealed class LlamaGrammarToolSchemaCompatibilityTests
{
    // Every keyword that drives GBNF repetition unrolling, one per test case. Measured on a real llama-server
    // (source-build CUDA b10201): maxLength 1990 compiles, 2000 fails; minLength/maxItems/minItems fail at 8000.
    [Test]
    [Arguments("maxLength")]
    [Arguments("minLength")]
    [Arguments("maxItems")]
    [Arguments("minItems")]
    public void Sanitize_WhenKeywordExceedsTheBound_RemovesOnlyThatKeyword(string keyword)
    {
        var schema = ParseDetached($$"""
                                     {
                                       "type": "object",
                                       "properties": {
                                         "value": { "type": "string", "{{keyword}}": 8000, "description": "kept" }
                                       }
                                     }
                                     """);

        var sanitized = LlamaGrammarToolSchemaCompatibility.Sanitize(schema);

        var property = sanitized.GetProperty("properties").GetProperty("value");
        AssertEx.False(property.TryGetProperty(keyword, out _), $"{keyword} above the bound must be removed from the wire schema.");
        AssertEx.Equal("string", property.GetProperty("type").GetString());
        AssertEx.Equal("kept", property.GetProperty("description").GetString(), "sibling keywords must survive untouched.");
    }

    [Test]
    [Arguments("maxLength")]
    [Arguments("minLength")]
    [Arguments("maxItems")]
    [Arguments("minItems")]
    public void Sanitize_WhenKeywordIsAtTheBound_PreservesIt(string keyword)
    {
        // Exactly at MaxGrammarRepetitionBound: the pass removes only what EXCEEDS the bound, so a schema authored to
        // the documented ceiling keeps advertising it.
        var schema = ParseDetached($$"""
                                     { "type": "object", "properties": { "value": { "type": "string", "{{keyword}}": {{LlamaGrammarToolSchemaCompatibility.MaxGrammarRepetitionBound}} } } }
                                     """);

        var sanitized = LlamaGrammarToolSchemaCompatibility.Sanitize(schema);

        AssertEx.Equal(LlamaGrammarToolSchemaCompatibility.MaxGrammarRepetitionBound,
            sanitized.GetProperty("properties").GetProperty("value").GetProperty(keyword).GetInt32(),
            $"{keyword} at the bound is compilable and must be preserved.");
    }

    [Test]
    [Arguments("^[a-z]{0,8000}$")]
    [Arguments("^[a-z]{2000}$")]
    [Arguments("^[a-z]{2000,}$")]
    public void Sanitize_WhenPatternQuantifierExceedsTheBound_DropsTheWholePatternKeyword(string pattern)
    {
        // A `pattern` has no partial form — llama.cpp unrolls its quantifiers the same way — so the keyword is dropped
        // whole. `{n,}` is judged on its lower bound: the open tail is emitted as a recursive rule, not by unrolling.
        var schema = ParseDetached($$"""
                                     { "type": "object", "properties": { "value": { "type": "string", "pattern": "{{pattern}}" } } }
                                     """);

        var sanitized = LlamaGrammarToolSchemaCompatibility.Sanitize(schema);

        AssertEx.False(sanitized.GetProperty("properties").GetProperty("value").TryGetProperty("pattern", out _),
            "a pattern whose quantifier exceeds the bound must be dropped whole.");
    }

    [Test]
    public void Sanitize_WhenPatternQuantifierIsWithinTheBound_PreservesIt()
    {
        // The real run_in_agent_home selectedFolderIds pattern. It must survive: it is compilable, and it is the only
        // thing steering the model toward a well-formed folder id.
        const string Pattern = "^[a-z0-9][a-z0-9-]{0,63}$|^[0-9a-fA-F-]{36}$";
        var schema = ParseDetached($$"""
                                     { "type": "object", "properties": { "value": { "type": "string", "pattern": "{{Pattern}}" } } }
                                     """);

        var sanitized = LlamaGrammarToolSchemaCompatibility.Sanitize(schema);

        AssertEx.Equal(Pattern, sanitized.GetProperty("properties").GetProperty("value").GetProperty("pattern").GetString());
    }

    [Test]
    public void Sanitize_NeverTouchesIntegerMinimumOrMaximum()
    {
        // Measured safe: `maximum: 100000` compiles. Integer bounds are value ranges, not repetition counts, so they are
        // deliberately outside this pass — clamping them here would silently narrow a tool's real accepted range.
        var schema = ParseDetached("""
                                   { "type": "object", "properties": { "count": { "type": "integer", "minimum": 100000, "maximum": 100000 } } }
                                   """);

        var sanitized = LlamaGrammarToolSchemaCompatibility.Sanitize(schema);

        var count = sanitized.GetProperty("properties").GetProperty("count");
        AssertEx.Equal(100000, count.GetProperty("minimum").GetInt32());
        AssertEx.Equal(100000, count.GetProperty("maximum").GetInt32());
    }

    [Test]
    public void Sanitize_RecursesIntoArrayItemsAndNestedProperties()
    {
        var schema = ParseDetached("""
                                   {
                                     "type": "object",
                                     "properties": {
                                       "folders": {
                                         "type": "array",
                                         "maxItems": 4000,
                                         "items": { "type": "string", "maxLength": 4096, "minLength": 1 }
                                       },
                                       "nested": {
                                         "type": "object",
                                         "properties": {
                                           "deep": { "type": "string", "maxLength": 8000 }
                                         }
                                       },
                                       "oneOf": [
                                         { "type": "string", "maxLength": 5000 },
                                         { "type": "string", "maxLength": 10 }
                                       ]
                                     }
                                   }
                                   """);

        var sanitized = LlamaGrammarToolSchemaCompatibility.Sanitize(schema);

        var folders = sanitized.GetProperty("properties").GetProperty("folders");
        AssertEx.False(folders.TryGetProperty("maxItems", out _), "the array's own oversized maxItems must be removed.");
        AssertEx.False(folders.GetProperty("items").TryGetProperty("maxLength", out _), "array items are sanitized recursively.");
        AssertEx.Equal(1, folders.GetProperty("items").GetProperty("minLength").GetInt32(), "an in-bounds sibling inside items survives.");
        AssertEx.False(sanitized.GetProperty("properties").GetProperty("nested").GetProperty("properties").GetProperty("deep").TryGetProperty("maxLength", out _),
            "nested object properties are sanitized recursively.");

        var alternatives = sanitized.GetProperty("properties").GetProperty("oneOf");
        AssertEx.False(alternatives[0].TryGetProperty("maxLength", out _), "JSON-array schema branches are sanitized recursively.");
        AssertEx.Equal(10, alternatives[1].GetProperty("maxLength").GetInt32(), "an in-bounds branch is left alone.");

        // Nothing above the bound is left anywhere in the tree — the compilability property the pass guarantees.
        AssertEx.False(LlamaGrammarToolSchemaCompatibility.RequiresSanitizing(sanitized));
    }

    [Test]
    public void Sanitize_WhenSchemaIsAlreadySafe_ReturnsTheVerySameElementInstance()
    {
        // The common path (every already-safe tool, on every request) must allocate nothing. Identity proof: the
        // returned element is asserted to still be bound to the INPUT's JsonDocument — a rebuilt schema would be a
        // detached clone with its own document and would survive the dispose below.
        var document = JsonDocument.Parse("""
                                          { "type": "object", "properties": { "value": { "type": "string", "maxLength": 1000, "pattern": "^[a-z]{1,63}$" } } }
                                          """);
        JsonElement sanitized;
        try
        {
            sanitized = LlamaGrammarToolSchemaCompatibility.Sanitize(document.RootElement);

            AssertEx.False(LlamaGrammarToolSchemaCompatibility.RequiresSanitizing(document.RootElement), "the fixture schema is already compilable.");
            AssertEx.Equal(document.RootElement.GetRawText(), sanitized.GetRawText());
        }
        finally
        {
            document.Dispose();
        }

        var stillBoundToInput = false;
        try
        {
            _ = sanitized.GetRawText();
        }
        catch (ObjectDisposedException)
        {
            stillBoundToInput = true;
        }

        AssertEx.True(stillBoundToInput, "an already-safe schema must be returned as the SAME element, not re-parsed into a new document.");
    }

    [Test]
    public void ApplyToolSchemaCompatibility_WhenNoToolNeedsSanitizing_ReturnsTheSameOptionsInstance()
    {
        var options = new ChatOptions
        {
            Tools = [BuildTool("safe_tool", """{ "type": "object", "properties": { "value": { "type": "string", "maxLength": 512 } } }""")]
        };

        var result = DeferredLlamaServerChatClient.ApplyToolSchemaCompatibility(options);

        AssertEx.True(ReferenceEquals(options, result),
            "with every schema already compilable the options must be returned unchanged, so the request stays byte-identical.");
    }

    [Test]
    public void ApplyToolSchemaCompatibility_WhenOptionsCarryNoTools_ReturnsTheSameOptionsInstance()
    {
        var options = new ChatOptions();

        AssertEx.True(ReferenceEquals(options, DeferredLlamaServerChatClient.ApplyToolSchemaCompatibility(options)));
        AssertEx.Null(DeferredLlamaServerChatClient.ApplyToolSchemaCompatibility(null));
    }

    [Test]
    public void ApplyToolSchemaCompatibility_SwapsOnlyTheOffendingTool_AndNeverMutatesTheCallersList()
    {
        // The load-bearing safety property. The swap happens on a CLONE, so the tool objects the layers above this
        // client hold — the function-invocation middleware's approval detection, and every `is ApprovalRequiredAIFunction`
        // type check — keep seeing their original instances. The wrapper exists only for wire serialization.
        var safe = BuildTool("safe_tool", """{ "type": "object", "properties": { "value": { "type": "string", "maxLength": 512 } } }""");
        var oversized = BuildTool("oversized_tool", """{ "type": "object", "properties": { "value": { "type": "string", "maxLength": 8000 } } }""");
        var callerTools = new List<AITool> { safe, oversized };
        var options = new ChatOptions { Tools = callerTools };

        var patched = AssertEx.NotNull(DeferredLlamaServerChatClient.ApplyToolSchemaCompatibility(options));

        AssertEx.False(ReferenceEquals(options, patched), "a tool needed sanitizing, so a clone must be returned.");
        AssertEx.True(ReferenceEquals(callerTools, options.Tools), "the caller's ChatOptions must still reference its own list.");
        AssertEx.True(ReferenceEquals(safe, callerTools[0]), "the caller's tool instances must be untouched.");
        AssertEx.True(ReferenceEquals(oversized, callerTools[1]), "the caller's tool instances must be untouched.");

        var patchedTools = AssertEx.NotNull(patched.Tools);
        AssertEx.True(ReferenceEquals(safe, patchedTools[0]), "a compilable tool is passed through by reference, not re-wrapped.");
        AssertEx.False(ReferenceEquals(oversized, patchedTools[1]), "the oversized tool is replaced in the clone.");

        // The wrapper is schema-only: name, description and invocation still come from the inner function.
        var wrapper = (AIFunction)patchedTools[1];
        AssertEx.Equal("oversized_tool", wrapper.Name);
        AssertEx.Equal(oversized.Description, wrapper.Description);
        AssertEx.False(wrapper.JsonSchema.GetProperty("properties").GetProperty("value").TryGetProperty("maxLength", out _));
        AssertEx.Equal(8000, oversized.JsonSchema.GetProperty("properties").GetProperty("value").GetProperty("maxLength").GetInt32(),
            "the handler-side schema — the one argument validation runs against — must keep its full bound.");
    }

    [Test]
    public void ProductionToolOffer_IsCompilableAfterThePass_AndCurrentlyNeedsIt()
    {
        var offered = BuildProductionToolOffer();
        AssertEx.NotEmpty(offered);

        // Guard against a vacuous pass: the real catalog must still contain at least one oversized bound today, or this
        // regression test would keep going green after the pass was removed.
        AssertEx.Contains(offered, tool => LlamaGrammarToolSchemaCompatibility.RequiresSanitizing(tool.JsonSchema));

        var options = new ChatOptions { Tools = [.. offered.Cast<AITool>()] };

        var patched = AssertEx.NotNull(DeferredLlamaServerChatClient.ApplyToolSchemaCompatibility(options));

        foreach (var tool in AssertEx.NotNull(patched.Tools).OfType<AIFunction>())
        {
            AssertEx.False(LlamaGrammarToolSchemaCompatibility.RequiresSanitizing(tool.JsonSchema),
                $"'{tool.Name}' still carries a bound llama.cpp cannot compile after the compatibility pass.");
        }
    }

    [Test]
    public async Task ProductionToolOffer_SerializesWithoutAnOversizedBound_OnTheRealWireBody()
    {
        // End-to-end through the REAL MEAI OpenAI adapter (the same client DeferredLlamaServerChatClient builds), so the
        // assertion is about the bytes llama-server would parse, not about an intermediate object.
        using var handler = new CapturingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var chat = BuildOpenAiChatClient(http);
        var options = new ChatOptions { Tools = [.. BuildProductionToolOffer().Cast<AITool>()] };

        var patched = DeferredLlamaServerChatClient.ApplyToolSchemaCompatibility(options);
        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], patched, CancellationToken.None);

        var body = AssertEx.NotNull(handler.CapturedBody);
        using var document = JsonDocument.Parse(body);
        var tools = document.RootElement.GetProperty("tools");
        AssertEx.False(LlamaGrammarToolSchemaCompatibility.RequiresSanitizing(tools),
            "the serialized tools array must carry no repetition bound above the compilable limit.");

        // Sanity: the offer really did reach the wire, so the assertion above is not passing over an empty array.
        AssertEx.Contains(tools.EnumerateArray().Select(static tool => tool.GetProperty("function").GetProperty("name").GetString() ?? string.Empty),
            "run_in_agent_home");
    }

    /// <summary>
    ///     Builds the REAL offered tool set — the built-in catalog plus the AgentHome / coder / knowledge-base / spawn
    ///     tools — as the executable <see cref="MetadataToolFunction" />s the invocation pipeline hands to the provider.
    /// </summary>
    private static IReadOnlyList<AIFunction> BuildProductionToolOffer()
    {
        const string Model = "qwen3:8b";
        var runtimeSettings = Substitute.For<INodeRuntimeSettings>();
        runtimeSettings.GetToolCapableModels().Returns(_ => new List<string> { Model });

        var provider = new LocalToolOfferProvider(new NodeCatalogAgentToolRegistry(),
            new McpToolRegistry(NullLogger<McpToolRegistry>.Instance),
            runtimeSettings,
            allowCloudKnowledgeAccess: false);

        // The profile pool is the widest offer (it adds spawn_subagent, whose 8000-char bounds are the largest we ship).
        return
        [
            .. provider.GetOfferedToolsForProfile(Model)
                       .Where(static tool => !string.IsNullOrWhiteSpace(tool.ParameterSchema))
                       .Select(static tool => BuildTool(tool.Name, tool.ParameterSchema!))
        ];
    }

    private static AIFunction BuildTool(string name, string parameterSchema)
    {
        return new MetadataToolFunction(name,
            $"{name} description",
            MetadataToolFunction.ParseSchema(parameterSchema),
            static (_, _) => Task.FromResult("ok"));
    }

    private static JsonElement ParseDetached(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
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

    /// <summary>
    ///     The node's built-in catalog as the offer provider sees it: the real <see cref="LocalAgentToolRegistry" />
    ///     descriptors plus <c>run_in_agent_home</c>, whose descriptor reaches a live node through the server
    ///     <c>ToolDefinition</c> seed rather than through the local registry. Its schema is the real
    ///     <see cref="AgentHomeToolDefinition.ParameterSchema" /> constant, so the 4000-char <c>goal</c> bound this pass
    ///     has to handle is the shipped one, not a fixture.
    /// </summary>
    private sealed class NodeCatalogAgentToolRegistry : IAgentToolRegistry
    {
        private static readonly LocalAgentToolRegistry Catalog = new();

        public IReadOnlyList<AITool> GetLocalChatTools()
        {
            return Catalog.GetLocalChatTools();
        }

        public IReadOnlyList<LocalChatToolDescriptor> GetLocalChatToolDescriptors()
        {
            return
            [
                .. Catalog.GetLocalChatToolDescriptors(),
                new LocalChatToolDescriptor(AgentHomeToolDefinition.ToolName,
                    AgentHomeToolDefinition.Description,
                    AgentHomeToolDefinition.ParameterSchema,
                    RequiresApproval: true,
                    ToolCategory.Unknown)
            ];
        }
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
