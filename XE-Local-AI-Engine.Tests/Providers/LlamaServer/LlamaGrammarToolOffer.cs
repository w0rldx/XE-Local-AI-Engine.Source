namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
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
///     The REAL production tool offer, plus the REAL MEAI OpenAI adapter that turns it into wire bytes. Shared by the
///     deterministic <see cref="LlamaGrammarToolSchemaCompatibilityTests" /> and the env-gated
///     <see cref="LlamaGrammarLiveSmokeTests" /> so both judge the same artifact: the offline test asserts a property of
///     the bytes, the live smoke posts those exact bytes to a real llama-server. Duplicating this would let the two
///     drift, and the live smoke's whole value is that it grades the shipped offer rather than a fixture.
/// </summary>
internal static class LlamaGrammarToolOffer
{
    /// <summary>
    ///     Builds the REAL offered tool set — the built-in catalog plus the AgentHome / coder / knowledge-base / spawn
    ///     tools — as the executable <see cref="MetadataToolFunction" />s the invocation pipeline hands to the provider.
    /// </summary>
    public static IReadOnlyList<AIFunction> BuildProductionToolOffer()
    {
        const string Model = "qwen3:8b";
        var runtimeSettings = Substitute.For<INodeRuntimeSettings>();
        runtimeSettings.GetToolCapableModels().Returns(_ => new List<string>
        {
            Model
        });

        var provider = new LocalToolOfferProvider(new NodeCatalogAgentToolRegistry(),
            new McpToolRegistry(NullLogger<McpToolRegistry>.Instance),
            runtimeSettings,
            NullCustomToolScopeFactory.Instance,
            new FakeModelTrustResolver(),
            allowCloudKnowledgeAccess: false);

        // The profile pool is the widest offer (it adds spawn_subagent, whose 8000-char bounds are the largest we ship),
        // PLUS emit_output. The profile offer excludes emit_output by design — only an integration execution is offered
        // it — so IntegrationExecutionCoordinator unions GetIntegrationOutputOffer() in at run time, exactly as here.
        // Without this union neither the offline compatibility tests nor the live smoke would ever compile the one
        // schema reachable from OUTSIDE the node, and its untyped `payload` subschema is the least ordinary one we ship.
        return
        [
            .. provider.GetOfferedToolsForProfile(Model)
                       .Concat(provider.GetIntegrationOutputOffer())
                       .Where(static tool => !string.IsNullOrWhiteSpace(tool.ParameterSchema))
                       .Select(static tool => BuildTool(tool.Name, tool.ParameterSchema!))
        ];
    }

    public static AIFunction BuildTool(string name, string parameterSchema)
    {
        return new MetadataToolFunction(name,
            $"{name} description",
            MetadataToolFunction.ParseSchema(parameterSchema),
            static (_, _) => Task.FromResult("ok"));
    }

    /// <summary>
    ///     Runs <paramref name="options" /> through the REAL MEAI OpenAI adapter — the same client
    ///     <c>DeferredLlamaServerChatClient</c> builds — against a handler that answers locally, and returns the request
    ///     body that would have gone out. This is the only way to obtain the bytes llama-server actually parses:
    ///     serializing the schemas by hand would grade a reimplementation of the adapter instead of the adapter.
    /// </summary>
    public static async Task<string> CaptureWireBodyAsync(ChatOptions options, CancellationToken cancellationToken = default)
    {
        return await CaptureWireBodyAsync([new ChatMessage(ChatRole.User, "hi")], options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     As <see cref="CaptureWireBodyAsync(ChatOptions,CancellationToken)" />, but for a caller-supplied message list —
    ///     so a test can grade the bytes an ACTUAL budgeted, approval-replayed history serializes to rather than just its
    ///     tool schemas. The same adapter, so the two capture paths cannot drift.
    /// </summary>
    public static async Task<string> CaptureWireBodyAsync(IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken = default)
    {
        using var handler = new CapturingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var chat = BuildOpenAiChatClient(BuildDefaultClientOptions(), http);

        await chat.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        return handler.CapturedBody
               ?? throw new InvalidOperationException("The OpenAI adapter produced no request body to capture.");
    }

    /// <summary>
    ///     The same capture through the PRODUCTION llama-server client options
    ///     (<see cref="LlamaServerOpenAIAdapterFactory.BuildClientOptions" />) rather than a locally-declared equivalent,
    ///     with only the transport swapped for the capturing handler. llama-server speaks the OpenAI completions shape, so
    ///     both providers share this adapter — capturing through both entry points is what proves a change on one lane did
    ///     not quietly move the bytes on the other.
    /// </summary>
    public static async Task<string> CaptureLlamaServerWireBodyAsync(IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken = default)
    {
        using var handler = new CapturingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var chat = BuildOpenAiChatClient(LlamaServerOpenAIAdapterFactory.BuildClientOptions(new Uri("http://127.0.0.1:1/v1"), TimeSpan.FromSeconds(30)),
            http);

        await chat.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        return handler.CapturedBody
               ?? throw new InvalidOperationException("The llama-server OpenAI adapter produced no request body to capture.");
    }

    private static OpenAIClientOptions BuildDefaultClientOptions()
    {
        return new OpenAIClientOptions
        {
            Endpoint = new Uri("http://127.0.0.1:1/v1"),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0)
        };
    }

    private static IChatClient BuildOpenAiChatClient(OpenAIClientOptions options, HttpClient http)
    {
        options.Transport = new HttpClientPipelineTransport(http);
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
