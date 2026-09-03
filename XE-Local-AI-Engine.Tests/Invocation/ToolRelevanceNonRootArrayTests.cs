namespace XE_Local_AI_Engine.Tests.Invocation;

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Regression for the gate that makes the tool-relevance filter inert on every array except the single agent's:
///     the hop refuses to filter an array that carries no <c>list_tools</c> instance, and only
///     <c>InvocationAgentFactory</c> appends one. An orchestration participant and a spawned sub-agent both build their
///     tool arrays through the SHARED <see cref="InvocationToolResolver" />, which is what these tests drive — so a
///     participant turn and a child turn above the threshold each send their own COMPLETE tool array, even under an
///     active scope.
/// </summary>
public sealed class ToolRelevanceNonRootArrayTests
{
    [Test]
    public async Task ResolveAsync_ForANonRootOffer_AppendsNoListToolsFunction()
    {
        var resolved = await ResolveTwentyToolsAsync(requiresApproval: false);

        AssertEx.Equal(expected: 20, resolved.Count);
        AssertEx.Empty(resolved.OfType<ListToolsFunction>(), "Only the single-agent factory appends the escape hatch.");
    }

    [Test]
    public async Task OrchestrationParticipantTurn_AboveTheThreshold_SendsItsOwnCompleteToolArray()
    {
        var resolved = await ResolveTwentyToolsAsync(requiresApproval: false);
        var options = new ChatOptions
        {
            Tools = resolved,
            // A participant builds its own ChatOptions and DOES set Instructions, unlike either root agent-build path.
            Instructions = "You are the reviewer participant."
        };

        var sent = await SendAsync(options);

        AssertEx.True(ReferenceEquals(options, sent), "A participant array carries no escape hatch, so it is never filtered.");
        AssertEx.Equal(expected: 20, sent!.Tools!.Count);
    }

    [Test]
    public async Task SpawnedSubAgentTurn_AboveTheThreshold_SendsItsOwnCompleteToolArray()
    {
        // A spawned child's curated set is approval-STRIPPED and, like a participant's, carries no list_tools. It also
        // runs under the parent's AsyncLocal, so it reaches the same scope — and must still send everything it has.
        var resolved = await ResolveTwentyToolsAsync(requiresApproval: false);
        var options = new ChatOptions
        {
            Tools = resolved,
            Instructions = "You are the child agent."
        };

        var sent = await SendAsync(options);

        AssertEx.True(ReferenceEquals(options, sent));
        AssertEx.Equal(expected: 20, sent!.Tools!.Count);
    }

    private static async Task<ChatOptions?> SendAsync(ChatOptions options)
    {
        using var inner = new CapturingChatClient();
        using var sut = new ToolRelevanceChatClient(inner, new LexicalToolRelevanceSelector(), new ToolRelevanceOptions());

        using (ToolRelevanceScope.BeginScope(active: true, new HashSet<string>(StringComparer.Ordinal)))
        {
            _ = await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "read the project file and summarise it")], options);
        }

        return inner.ReceivedOptions.Single();
    }

    private static async Task<IList<AITool>> ResolveTwentyToolsAsync(bool requiresApproval)
    {
        var names = Enumerable.Range(0, 20).Select(static index => $"tool_{index}").ToList();

        // Hand-written fakes rather than NSubstitute: these AI.Agent interfaces are internal, and Castle DynamicProxy
        // cannot proxy an internal type without an InternalsVisibleTo("DynamicProxyGenAssembly2") this repo does not add.
        return await InvocationToolResolver.ResolveAsync([.. names.Select(name => InvocationToolBridge.CreateOfferPlaceholder(name, requiresApproval))],
            new FakeAgentToolRegistry(names),
            new EmptyClientLocalToolRegistry(),
            new EmptyMcpToolRegistry(),
            new EmptyCustomToolCatalog(),
            NullLogger.Instance);
    }

    private sealed class FakeAgentToolRegistry : IAgentToolRegistry
    {
        private readonly IReadOnlyList<AITool> _tools;

        public FakeAgentToolRegistry(IEnumerable<string> names)
        {
            _tools = [.. names.Select(static name => AIFunctionFactory.Create(() => "ok", name, $"The {name} tool."))];
        }

        public IReadOnlyList<AITool> GetLocalChatTools()
        {
            return _tools;
        }

        public IReadOnlyList<LocalChatToolDescriptor> GetLocalChatToolDescriptors()
        {
            return [];
        }
    }

    private sealed class EmptyClientLocalToolRegistry : IClientLocalToolRegistry
    {
        public bool TryResolve(string toolName, [NotNullWhen(true)] out AITool? tool)
        {
            tool = null;
            return false;
        }
    }

    private sealed class EmptyMcpToolRegistry : IMcpToolRegistry
    {
        public bool TryResolve(string name, [NotNullWhen(true)] out AITool? tool)
        {
            tool = null;
            return false;
        }

        public IReadOnlyList<LocalChatToolDescriptor> GetDescriptors()
        {
            return [];
        }

        public void ReplaceSnapshot(IReadOnlyList<McpRegisteredTool> tools)
        {
            // Never called: this test resolves an offer, it does not publish an MCP snapshot.
        }
    }

    private sealed class EmptyCustomToolCatalog : ICustomToolCatalog
    {
        public Task<IReadOnlyList<LocalChatToolDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalChatToolDescriptor>>([]);
        }

        public Task<AITool?> TryResolveAsync(string name, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<AITool?>(null);
        }
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public List<ChatOptions?> ReceivedOptions { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ReceivedOptions.Add(options);
            return Task.FromResult(new ChatResponse());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            ReceivedOptions.Add(options);
            await Task.Yield();
            yield break;
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
