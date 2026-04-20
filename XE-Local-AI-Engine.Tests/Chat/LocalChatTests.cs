namespace XE_Local_AI_Engine.Tests.Chat;

using System.Reflection;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LocalChatTests
{
    [Test]
    public async Task DisposeAsync_DoesNotDisposeInjectedChatService()
    {
        var componentType = Type.GetType("XE_Local_AI_Engine.Client.Components.Pages.Chat.LocalChat, XE-Local-AI-Engine.Client");
        AssertEx.NotNull(componentType);
        var resolvedComponentType = componentType!;

        var component = Activator.CreateInstance(resolvedComponentType, nonPublic: true);
        AssertEx.NotNull(component);
        var resolvedComponent = component!;

        await using var chatService = new RecordingLocalAgentChatService();
        var chatServiceProperty = resolvedComponentType.GetProperty("ChatService", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        AssertEx.NotNull(chatServiceProperty);
        chatServiceProperty!.SetValue(resolvedComponent, chatService);

        var disposeAsyncMethod = resolvedComponentType.GetMethod("DisposeAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        AssertEx.NotNull(disposeAsyncMethod);

        var disposeTask = (ValueTask)disposeAsyncMethod!.Invoke(resolvedComponent, null)!;
        await disposeTask;

        AssertEx.Equal(0, chatService.DisposeAsyncCallCount);
    }

    private sealed class RecordingLocalAgentChatService : ILocalAgentChatService
    {
        public int DisposeAsyncCallCount { get; private set; }

        public string SelectedModel => "test-model";

        public ValueTask DisposeAsync()
        {
            DisposeAsyncCallCount++;
            return ValueTask.CompletedTask;
        }

        public Task ResetSessionAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<string> SendMessageAsync(string userMessage, CancellationToken cancellationToken = default)
        {
            return AsyncEnumerable.Empty<string>();
        }

        public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
