namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

/// <summary>
///     A pinned thinking budget, from the factory's options to the bytes on the wire: under thinking off it must leave
///     the thinking switch's zero budget alone and arm no reasoning control; under a graded effort it is the budget sent.
/// </summary>
/// <remarks>
///     The factory builds the options, the deferred client sends them to a loopback server, and the captured body is
///     read, once per entry point: the bug lived between the two, so neither side alone can show it.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class DeferredLlamaServerPinnedBudgetWireTests
{
    private const int PinnedBudget = 4096;

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ThinkingOffWithAPin_SendsTheZeroBudget_AndArmsNoReasoningControl(bool streaming)
    {
        using var body = await SendAsync("none", streaming);

        AssertEx.False(body.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        AssertEx.Equal(0, body.RootElement.GetProperty("reasoning_budget_tokens").GetInt32(), "the pin must not overwrite the off switch's zero budget");
        AssertEx.False(body.RootElement.TryGetProperty("reasoning_control", out _), "nothing to end early when thinking is off");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task AGradedEffortWithAPin_SendsThePin(bool streaming)
    {
        using var body = await SendAsync("high", streaming);

        AssertEx.Equal(PinnedBudget, body.RootElement.GetProperty("reasoning_budget_tokens").GetInt32());
        AssertEx.False(body.RootElement.TryGetProperty("chat_template_kwargs", out _));
    }

    private static async Task<JsonDocument> SendAsync(string effort, bool streaming)
    {
        var options = await FactoryOptionsAsync(effort);
        using var server = streaming
            ? CapturingServer.Start(StreamingBody, "text/event-stream")
            : CapturingServer.Start(CompletionBody, "application/json");
        using var client = new DeferredLlamaServerChatClient(new FakeProcessSupervisor
        {
            EnsureEndpoint = server.BaseAddress
        }, "qwen3:8b", TimeSpan.FromSeconds(30));

        if (streaming)
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, CancellationToken.None))
            {
                // Drained so the request is sent; the body is what this pins.
            }
        }
        else
        {
            _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, CancellationToken.None);
        }

        return JsonDocument.Parse(AssertEx.NotNull(server.CapturedBody));
    }

    private static async Task<ChatOptions> FactoryOptionsAsync(string effort)
    {
        using var chatClient = new NullChatClient();
        var factory = new InvocationAgentFactory(chatClient,
            Options.Create(new InvocationAgentOptions()),
            NullLogger<InvocationAgentFactory>.Instance,
            NullLoggerFactory.Instance,
            EmptyServiceProvider.Instance,
            new LocalAgentToolRegistry(TimeProvider.System),
            new EmptyClientLocalToolRegistry(),
            new McpToolRegistry(NullLogger<McpToolRegistry>.Instance),
            new EmptyCustomToolCatalog());
        await using var context = await factory.CreateAsync(new InvocationAgentDefinition
        {
            ModelId = "qwen3:8b",
            Instructions = "Be helpful.",
            Tools = [],
            ConversationContext = [],
            ReasoningEffort = effort,
            Sampling = new InvocationSamplingOptions
            {
                ReasoningBudgetTokens = PinnedBudget
            }
        });
        return AssertEx.NotNull(((ChatClientAgentRunOptions)AssertEx.NotNull(context.RunOptions)).ChatOptions);
    }

    private const string CompletionBody =
        "{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":0,\"model\":\"qwen3:8b\","
        + "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}";

    private const string StreamingBody =
        "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"qwen3:8b\","
        + "\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n"
        + "data: [DONE]\n\n";

    /// <summary>The factory only holds the agent's client; options are what this reads, so nothing is ever sent through it.</summary>
    private sealed class NullChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The factory test never runs the agent.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The factory test never runs the agent.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class EmptyCustomToolCatalog : ICustomToolCatalog
    {
        public Task<IReadOnlyList<LocalChatToolDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalChatToolDescriptor>>([]);

        public Task<IReadOnlyDictionary<string, AITool>> TryResolveManyAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, AITool>>(new Dictionary<string, AITool>(StringComparer.Ordinal));
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();

        public object? GetService(Type serviceType) => null;
    }
}
