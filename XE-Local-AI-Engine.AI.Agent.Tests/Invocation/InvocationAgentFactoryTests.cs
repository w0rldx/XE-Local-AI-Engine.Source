namespace XE_Local_AI_Engine.AI.Agent.Tests.Invocation;

using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class InvocationAgentFactoryTests
{
    [Test]
    public async Task CreateAsync_ReturnsContextWithSeedMessages()
    {
        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            "Be helpful.",
            [],
            [new ChatMessage(ChatRole.User, "hello")]);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        AssertEx.NotNull(context.Agent);
        AssertEx.Equal(2, context.SeedMessages.Count);
        AssertEx.Equal(ChatRole.System, context.SeedMessages[0].Role);
        AssertEx.Equal("Be helpful.", context.SeedMessages[0].Text);
        AssertEx.Equal("hello", context.SeedMessages[1].Text);
        AssertEx.Equal(false, context.Items["toolsEnabled"]);
    }

    [Test]
    public async Task CreateAsync_AppliesResolvedModelToChatOptions()
    {
        var definition = new InvocationAgentDefinition("llama3.2:3b",
            "Be helpful.",
            [],
            []);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var runOptions = context.RunOptions as ChatClientAgentRunOptions
                         ?? throw new AssertionException("Expected ChatClientAgentRunOptions.");
        var chatOptions = runOptions.ChatOptions
                          ?? throw new AssertionException("Expected ChatOptions to be populated.");

        AssertEx.Equal("llama3.2:3b", chatOptions.ModelId);
        var additionalProperties = AssertEx.NotNull(chatOptions.AdditionalProperties);
        AssertEx.True(additionalProperties.TryGetValue<bool>("think", out var thinkValue));
        AssertEx.Equal(true, thinkValue);
    }

    [Test]
    public async Task CreateAsync_WithOfferedNameInRegistry_EnablesToolsAndResolvesExecutable()
    {
        var registry = new FakeToolRegistry(AIFunctionFactory.Create((string input) => input, "Calculate"));
        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            "Be helpful.",
            [InvocationToolBridge.CreateOfferPlaceholder("Calculate")],
            []);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient, registry);

        await using var context = await sut.CreateAsync(definition);

        AssertEx.NotNull(context.Agent);
        AssertEx.Equal(true, context.Items["toolsEnabled"]);
    }

    [Test]
    public async Task CreateAsync_WithOfferedNameNotInRegistry_SkipsToolAndDisablesTools()
    {
        var registry = new FakeToolRegistry(AIFunctionFactory.Create((string input) => input, "Calculate"));
        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            "Be helpful.",
            [InvocationToolBridge.Create("echo", (input, _) => Task.FromResult(input))],
            []);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient, registry);

        await using var context = await sut.CreateAsync(definition);

        AssertEx.NotNull(context.Agent);
        AssertEx.Equal(false, context.Items["toolsEnabled"]);
    }

    [Test]
    public async Task CreateAsync_WithOfferedClientLocalName_ResolvesFromClientLocalRegistry()
    {
        var clientLocalRegistry = new FakeClientLocalToolRegistry(
            AIFunctionFactory.Create((string input) => input, "run_in_agent_home"));
        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            "Be helpful.",
            [InvocationToolBridge.CreateOfferPlaceholder("run_in_agent_home")],
            []);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient, clientLocalToolRegistry: clientLocalRegistry);

        await using var context = await sut.CreateAsync(definition);

        AssertEx.NotNull(context.Agent);
        AssertEx.Equal(true, context.Items["toolsEnabled"]);
    }

    [Test]
    public async Task CreateAsync_WithApprovalRequiredClientLocalTool_ResolvesApprovalWrappedHandler()
    {
        // End-to-end: a ClientLocal tool offered via the envelope path (an offer placeholder) is
        // resolved against the REAL ClientLocalToolRegistry, which wraps a RequiresApproval=true handler in an
        // ApprovalRequiredAIFunction. Prove the wrapped handler flows through the offer→resolve path without being
        // dropped, so the agent builds with tools enabled.
        var registry = new ClientLocalToolRegistry(
            [new ApprovalRequiredFakeHandler("run_in_agent_home", "Runs an agent task.", """{"type":"object"}""")]);
        var resolved = registry.TryResolve("run_in_agent_home", out var wrapped);
        AssertEx.True(resolved);
        AssertEx.True(wrapped is ApprovalRequiredAIFunction, "the high-risk handler must resolve approval-wrapped");

        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            "Be helpful.",
            [InvocationToolBridge.CreateOfferPlaceholder("run_in_agent_home")],
            []);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient, clientLocalToolRegistry: registry);

        await using var context = await sut.CreateAsync(definition);

        AssertEx.NotNull(context.Agent);
        AssertEx.Equal(true, context.Items["toolsEnabled"]);
    }

    [Test]
    public async Task CreateAsync_WithOfferedNameInNeitherRegistry_DisablesTools()
    {
        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            "Be helpful.",
            [InvocationToolBridge.CreateOfferPlaceholder("run_in_agent_home")],
            []);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        AssertEx.Equal(false, context.Items["toolsEnabled"]);
    }

    [Test]
    public async Task CreateAsync_WithOfferedMcpName_ResolvesFromMcpRegistry()
    {
        // Option C: an offered MCP-qualified name that matches neither the built-in nor the ClientLocal registry
        // resolves against the MCP tool registry's cached executable.
        var mcpRegistry = new FakeMcpToolRegistry(
            AIFunctionFactory.Create((string input) => input, "mcp__weather__get_forecast"));
        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            "Be helpful.",
            [InvocationToolBridge.CreateOfferPlaceholder("mcp__weather__get_forecast")],
            []);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient, mcpToolRegistry: mcpRegistry);

        await using var context = await sut.CreateAsync(definition);

        AssertEx.NotNull(context.Agent);
        AssertEx.Equal(true, context.Items["toolsEnabled"]);
    }

    [Test]
    public async Task CreateAsync_WithApprovalWrappedMcpTool_ResolvesWrappedExecutable()
    {
        // An MCP tool is registered approval-wrapped (the catalog default). The factory must resolve the wrapped
        // executable through Option C so the approval gate survives the offer -> resolve path.
        var wrapped = new ApprovalRequiredAIFunction(
            AIFunctionFactory.Create((string input) => input, "mcp__files__write_file"));
        var snapshot = new[]
        {
            new McpRegisteredTool("mcp__files__write_file",
                wrapped,
                new LocalChatToolDescriptor("mcp__files__write_file", "Writes a file.", """{"type":"object"}""", true))
        };
        var mcpRegistry = new FakeMcpToolRegistry();
        mcpRegistry.ReplaceSnapshot(snapshot);

        var resolved = mcpRegistry.TryResolve("mcp__files__write_file", out var executable);
        AssertEx.True(resolved);
        AssertEx.True(executable is ApprovalRequiredAIFunction, "the MCP tool must resolve approval-wrapped");

        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            "Be helpful.",
            [InvocationToolBridge.CreateOfferPlaceholder("mcp__files__write_file")],
            []);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient, mcpToolRegistry: mcpRegistry);

        await using var context = await sut.CreateAsync(definition);

        AssertEx.NotNull(context.Agent);
        AssertEx.Equal(true, context.Items["toolsEnabled"]);
    }

    [Test]
    public async Task CreateAsync_OrdersConversationContext_WhenBuildingSeedMessages()
    {
        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            "Be helpful.",
            [],
            [
                new ChatMessage(ChatRole.User, "first"),
                new ChatMessage(ChatRole.Assistant, "second")
            ]);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        AssertEx.Equal("first", context.SeedMessages[1].Text);
        AssertEx.Equal("second", context.SeedMessages[2].Text);
    }

    private static InvocationAgentFactory CreateSut(FakeChatClient chatClient,
        IAgentToolRegistry? toolRegistry = null,
        IClientLocalToolRegistry? clientLocalToolRegistry = null,
        IMcpToolRegistry? mcpToolRegistry = null)
    {
        return new InvocationAgentFactory(chatClient,
            Options.Create(new InvocationAgentOptions()),
            NullLogger<InvocationAgentFactory>.Instance,
            NullLoggerFactory.Instance,
            FakeServiceProvider.Instance,
            toolRegistry ?? new FakeToolRegistry(),
            clientLocalToolRegistry ?? new FakeClientLocalToolRegistry(),
            mcpToolRegistry ?? new FakeMcpToolRegistry());
    }

    private sealed class FakeToolRegistry : IAgentToolRegistry
    {
        private readonly IReadOnlyList<AITool> _tools;

        public FakeToolRegistry(params AITool[] tools)
        {
            _tools = tools;
        }

        public IReadOnlyList<AITool> GetLocalChatTools()
        {
            return _tools;
        }

        public IReadOnlyList<LocalChatToolDescriptor> GetLocalChatToolDescriptors()
        {
            return
            [
                .. _tools.OfType<AIFunction>()
                         .Select(static function => new LocalChatToolDescriptor(function.Name, function.Description, function.JsonSchema.GetRawText(), false))
            ];
        }
    }

    private sealed class FakeClientLocalToolRegistry : IClientLocalToolRegistry
    {
        private readonly Dictionary<string, AITool> _tools = new(StringComparer.Ordinal);

        public FakeClientLocalToolRegistry(params AITool[] tools)
        {
            foreach (var function in tools.OfType<AIFunction>())
            {
                _tools[function.Name] = function;
            }
        }

        public bool TryResolve(string toolName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AITool? tool)
        {
            return _tools.TryGetValue(toolName, out tool);
        }
    }

    private sealed class FakeMcpToolRegistry : IMcpToolRegistry
    {
        private readonly Dictionary<string, AITool> _tools = new(StringComparer.Ordinal);

        public FakeMcpToolRegistry(params AITool[] tools)
        {
            foreach (var function in tools.OfType<AIFunction>())
            {
                _tools[function.Name] = function;
            }
        }

        public bool TryResolve(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AITool? tool)
        {
            return _tools.TryGetValue(name, out tool);
        }

        public IReadOnlyList<LocalChatToolDescriptor> GetDescriptors()
        {
            return
            [
                .. _tools.Values.OfType<AIFunction>()
                         .Select(static function => new LocalChatToolDescriptor(function.Name, function.Description, function.JsonSchema.GetRawText(), true))
            ];
        }

        public void ReplaceSnapshot(IReadOnlyList<McpRegisteredTool> tools)
        {
            _tools.Clear();
            foreach (var tool in tools)
            {
                _tools[tool.Name] = tool.Executable;
            }
        }
    }

    private sealed class ApprovalRequiredFakeHandler(string toolName, string description, string parameterSchema)
        : IClientLocalToolHandler
    {
        public string ToolName => toolName;

        public string Description => description;

        public string ParameterSchema => parameterSchema;

        public bool RequiresApproval => true;

        public Task<string> ExecuteAsync(string jsonArguments, CancellationToken cancellationToken = default)
        {
            return Task.FromResult("ok");
        }
    }

    private sealed class FakeServiceProvider : IServiceProvider
    {
        public static FakeServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType)
        {
            return null;
        }
    }

    private sealed class FakeChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType == typeof(IChatClient) ? this : null;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}
