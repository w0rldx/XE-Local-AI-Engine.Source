namespace XE_Local_AI_Engine.Tests.Memory;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Memory;
using XE_Local_AI_Engine.Client.Services.Memory.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The privacy gate for adaptive-memory extraction: the agent MUST resolve a NODE-LOCAL provider via
///     <see cref="ILocalModelProviderResolver" /> and run the model on the client that provider produces — never a shared
///     cloud-capable <see cref="IChatClient" />. The agent has no <c>IChatClient</c> dependency at all (structural
///     guarantee); these tests prove it routes through the node-local resolver and disables cleanly without a model.
/// </summary>
public sealed class DefaultMemoryExtractionAgentTests
{
    [Test]
    public async Task MemoryExtraction_NeverUsesCloudChatClient()
    {
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns("llamacpp");

#pragma warning disable CA2000 // Ownership transfers to the agent, which disposes it via `using`; the test asserts IsDisposed.
        var nodeLocalClient = new EnvelopeChatClient("""
                                                     { "memories": [ { "behavior": "Prefer the shared helper.",
                                                       "scope": "procedural", "triggerCondition": null, "confidence": 0.9 } ] }
                                                     """);
#pragma warning restore CA2000
        provider.CreateChatClient(Arg.Any<LocalModelSelection>()).Returns(nodeLocalClient);
        resolver.ResolveProviderForModelAsync("qwen3:8b", Arg.Any<CancellationToken>()).Returns(Task.FromResult(provider));

        var agent = new DefaultMemoryExtractionAgent(resolver,
            Options.Create(new MemoryExtractionOptions
            {
                ExtractionModelName = "qwen3:8b"
            }),
            NullLogger<DefaultMemoryExtractionAgent>.Instance);

        var proposals = await agent.ProposeAsync(Run()).ConfigureAwait(false);

        // Routed through the node-local resolver, and the model ran on the node-local provider's client.
        await resolver.Received(1).ResolveProviderForModelAsync("qwen3:8b", Arg.Any<CancellationToken>()).ConfigureAwait(false);
        provider.Received(1).CreateChatClient(Arg.Is<LocalModelSelection>(selection =>
            selection.ModelName == "qwen3:8b" && selection.ProviderName == "llamacpp"));
        AssertEx.True(nodeLocalClient.WasCalled, "The extraction model must run on the node-local provider's client.");
        AssertEx.True(nodeLocalClient.IsDisposed, "The per-run node-local chat client must be disposed.");
        AssertEx.Equal(expected: 1, proposals.Count);
        AssertEx.Equal(MemoryScope.Procedural, proposals[0].Scope);
    }

    [Test]
    public async Task MemoryExtraction_WhenNoModelConfigured_ResolvesNothingAndReturnsEmpty()
    {
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        var agent = new DefaultMemoryExtractionAgent(resolver,
            Options.Create(new MemoryExtractionOptions
            {
                ExtractionModelName = string.Empty
            }),
            NullLogger<DefaultMemoryExtractionAgent>.Instance);

        var proposals = await agent.ProposeAsync(Run()).ConfigureAwait(false);

        AssertEx.Empty(proposals);
        // The disabled gate must short-circuit before any provider resolution (no node-local client constructed at all).
        await resolver.DidNotReceive().ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task MemoryExtraction_WhenRunSucceeded_DropsFailureScopeCandidate()
    {
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns("llamacpp");
        // The model mislabels a successful run as a failure lesson — the agent must drop it.
#pragma warning disable CA2000 // Ownership transfers to the agent, which disposes it via `using`.
        var nodeLocalClient = new EnvelopeChatClient("""
                                                     { "memories": [ { "behavior": "Avoid X.", "scope": "failure", "confidence": 0.7 } ] }
                                                     """);
#pragma warning restore CA2000
        provider.CreateChatClient(Arg.Any<LocalModelSelection>()).Returns(nodeLocalClient);
        resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(provider));

        var agent = new DefaultMemoryExtractionAgent(resolver,
            Options.Create(new MemoryExtractionOptions
            {
                ExtractionModelName = "qwen3:8b"
            }),
            NullLogger<DefaultMemoryExtractionAgent>.Instance);

        var proposals = await agent.ProposeAsync(Run(failed: false)).ConfigureAwait(false);

        AssertEx.Empty(proposals);
    }

    private static MemoryExtractionRunInput Run(bool failed = false)
    {
        return new MemoryExtractionRunInput(Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            [new MemoryExtractionTurn("How do I add a feature?")],
            "Use the shared helper.",
            failed,
            failed ? "tool-failed" : null,
            MemoryExcluded: false);
    }

    /// <summary>
    ///     A minimal node-local <see cref="IChatClient" /> stand-in returning a fixed JSON envelope so the agent's
    ///     <c>GetResponseAsync&lt;ExtractionEnvelope&gt;</c> parses a structured result without a live model.
    /// </summary>
    private sealed class EnvelopeChatClient(string json) : IChatClient
    {
        public bool WasCalled { get; private set; }

        public bool IsDisposed { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            // Not exercised by these tests (the agent uses the non-streaming GetResponseAsync); an empty stream suffices.
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceType.IsInstanceOfType(this) && serviceKey is null ? this : null;
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
