namespace XE_Local_AI_Engine.Tests.Invocation;

using System.Collections.ObjectModel;
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
    public async Task AnArrayWithoutAListToolsFunction_AboveTheThreshold_IsSentWholeAndUnchanged()
    {
        // ONE test, not two: an orchestration participant's array and a spawned sub-agent's curated (approval-stripped)
        // array are the same scenario at this seam — both are resolved through InvocationToolResolver, both set
        // Instructions, both run under the parent's AsyncLocal scope, and neither carries a list_tools instance. What
        // separates them lives above this hop, so a second copy of this test would have graded nothing extra. The
        // "only one product site appends list_tools" half of the property is pinned by
        // Architecture/ToolRelevanceOfferArchitectureTests.
        var resolved = await ResolveTwentyToolsAsync(requiresApproval: false);
        var options = new ChatOptions
        {
            Tools = resolved,
            Instructions = "You are the reviewer participant."
        };

        var sent = await SendAsync(options);

        AssertEx.True(ReferenceEquals(options, sent), "An array with no escape hatch is never filtered — the same instance is sent on.");
        AssertEx.Equal(expected: 20, sent!.Tools!.Count);
    }

    private static async Task<ChatOptions?> SendAsync(ChatOptions options)
    {
        using var inner = new CapturingChatClient();
        using var sut = new ToolRelevanceChatClient(inner,
            new LexicalToolRelevanceSelector(),
            new ToolRelevanceOptions(),
            NullLogger<ToolRelevanceChatClient>.Instance);

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

        public Task<IReadOnlyDictionary<string, AITool>> TryResolveManyAsync(IReadOnlyCollection<string> names,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyDictionary<string, AITool>>(ReadOnlyDictionary<string, AITool>.Empty);
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
