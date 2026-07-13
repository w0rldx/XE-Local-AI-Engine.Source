namespace XE_Local_AI_Engine.AI.Agent.Tests.Chat;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ProviderCallBudgetChatClientTests
{
    [Test]
    public async Task GetResponseAsync_WithoutAmbientBudget_PassesMessagesThroughUnchanged()
    {
        using var inner = new CapturingChatClient();
        using var sut = new ProviderCallBudgetChatClient(inner, NullLogger<ProviderCallBudgetChatClient>.Instance);

        var messages = ManyMessagesWithHugeToolResult();
        _ = await sut.GetResponseAsync(messages, SmallWindowOptions());

        // No scope was seeded, so the middleware is a transparent pass-through (eval / preview paths stay byte-identical).
        var received = inner.ReceivedMessageSets.Single();
        AssertEx.Equal(messages.Count, received.Count);
    }

    [Test]
    public async Task GetResponseAsync_WithAmbientBudget_ReBudgetsEachProviderRoundBeforeSending()
    {
        using var inner = new CapturingChatClient();
        using var sut = new ProviderCallBudgetChatClient(inner, NullLogger<ProviderCallBudgetChatClient>.Instance);

        var messages = ManyMessagesWithHugeToolResult();
        using (ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions { OversizedToolResultExcerptChars = 40 }))
        {
            // Simulates an inner tool-loop round: FunctionInvokingChatClient appended a huge tool result and called the
            // provider again. The boundary must re-budget it (the outer runner never sees this round).
            _ = await sut.GetResponseAsync(messages, SmallWindowOptions());
        }

        var received = inner.ReceivedMessageSets.Single();
        var pending = received.SelectMany(message => message.Contents.OfType<FunctionResultContent>())
                             .First(content => string.Equals(content.CallId, "big", StringComparison.Ordinal));
        AssertEx.Contains(pending.Result?.ToString() ?? string.Empty, "[truncated:");
    }

    [Test]
    public async Task GetResponseAsync_WhenCumulativeCallCeilingExceeded_ThrowsTypedError()
    {
        using var inner = new CapturingChatClient();
        using var sut = new ProviderCallBudgetChatClient(inner, NullLogger<ProviderCallBudgetChatClient>.Instance);
        var messages = new List<ChatMessage> { new(ChatRole.User, "hi") };

        using (ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions { MaxProviderCallsPerInvocation = 2 }))
        {
            _ = await sut.GetResponseAsync(messages);
            _ = await sut.GetResponseAsync(messages);

            // The third round trips the cumulative call ceiling — a runaway loop is terminated with a typed error
            // BEFORE the provider is called, rather than hanging.
            _ = await AssertEx.ThrowsAsync<ProviderCallBudgetExceededException>(async () => await sut.GetResponseAsync(messages));
        }

        AssertEx.Equal(expected: 2, inner.ReceivedMessageSets.Count);
    }

    [Test]
    public async Task GetResponseAsync_WhenCumulativeTokenCeilingExceeded_ThrowsTypedError()
    {
        using var inner = new CapturingChatClient();
        using var sut = new ProviderCallBudgetChatClient(inner, NullLogger<ProviderCallBudgetChatClient>.Instance);

        // A single large round (~2004 estimated tokens) exceeds a tiny cumulative-token ceiling.
        var messages = new List<ChatMessage> { new(ChatRole.User, new string('x', 8000)) };
        using (ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions { MaxCumulativeInputTokens = 1024, DefaultContextTokens = 1_000_000 }))
        {
            _ = await AssertEx.ThrowsAsync<ProviderCallBudgetExceededException>(async () => await sut.GetResponseAsync(messages, new ChatOptions()));
        }
    }

    private static ChatOptions SmallWindowOptions()
    {
        return new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["num_ctx"] = 200 }
        };
    }

    private static List<ChatMessage> ManyMessagesWithHugeToolResult()
    {
        return
        [
            new ChatMessage(ChatRole.System, "system prompt"),
            new ChatMessage(ChatRole.User, "please search"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("big", "search", new Dictionary<string, object?>())]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("big", new string('y', 4000))])
        ];
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public List<IReadOnlyList<ChatMessage>> ReceivedMessageSets { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ReceivedMessageSets.Add([.. messages]);
            return Task.FromResult(new ChatResponse());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            ReceivedMessageSets.Add([.. messages]);
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
