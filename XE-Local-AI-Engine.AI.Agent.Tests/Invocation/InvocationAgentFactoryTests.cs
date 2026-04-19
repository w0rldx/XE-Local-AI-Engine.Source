namespace XE_Local_AI_Engine.AI.Agent.Tests.Invocation;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class InvocationAgentFactoryTests
{
    [Test]
    public async Task CreateAsync_ReturnsContextWithSeedMessages()
    {
        var definition = new InvocationAgentDefinition("qwen3.5:9b",
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
    public async Task CreateAsync_WithNonEmptyTools_IgnoresToolsAndReturnsContext()
    {
        var definition = new InvocationAgentDefinition("qwen3.5:9b",
            "Be helpful.",
            [InvocationToolBridge.Create("echo", (input, _) => Task.FromResult(input))],
            []);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        AssertEx.NotNull(context.Agent);
        AssertEx.Equal(false, context.Items["toolsEnabled"]);
    }

    [Test]
    public async Task CreateAsync_OrdersConversationContext_WhenBuildingSeedMessages()
    {
        var definition = new InvocationAgentDefinition("qwen3.5:9b",
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

    private static InvocationAgentFactory CreateSut(FakeChatClient chatClient)
    {
        return new InvocationAgentFactory(chatClient,
            Options.Create(new InvocationAgentOptions()),
            NullLogger<InvocationAgentFactory>.Instance,
            NullLoggerFactory.Instance,
            FakeServiceProvider.Instance);
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
