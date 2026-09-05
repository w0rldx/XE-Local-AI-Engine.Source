namespace XE_Local_AI_Engine.Tests.Chat;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class NodeChatStreamCancellationRegistryTests
{
    [Test]
    public async Task Dispose_WhenCancelAlreadyLookedUp_DoesNotCompleteUntilTheMatchingCallbackFinishes()
    {
        var registry = new NodeChatStreamCancellationRegistry();
        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = registry.Register(correlation, () =>
        {
            callbackEntered.SetResult();
            releaseCallback.Task.GetAwaiter().GetResult();
        });

        var cancelTask = Task.Run(() => registry.TryCancel(correlation));
        await callbackEntered.Task.ConfigureAwait(false);
        var disposeTask = Task.Run(registration.Dispose);

        await AssertEx.StaysIncompleteAsync(disposeTask,
            "Once cancellation claims a live registration, disposal must not report completion while its callback can still execute.")
                      .ConfigureAwait(false);

        releaseCallback.SetResult();
        AssertEx.True(await cancelTask.ConfigureAwait(false));
        await disposeTask.ConfigureAwait(false);
        AssertEx.False(registry.TryCancel(correlation), "A completed disposal must make the registration undiscoverable.");
    }
}
