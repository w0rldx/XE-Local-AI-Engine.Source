namespace XE_Local_AI_Engine.Tests.DeadLetter;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Models;
using XE_Local_AI_Engine.Services.Connection;
using XE_Local_AI_Engine.Services.DeadLetter;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

public sealed class DeadLetterFlushServiceTests
{
    [Test]
    public async Task FlushAsync_WhenNoPendingItems_DoesNotCallSender()
    {
        var store = Substitute.For<IDeadLetterStore>();
        store.GetPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<InvocationFailedPayload>>([]));
        var sender = new MockHubMessageSender();
        var service = CreateService(store, sender);

        await service.FlushAsync();

        AssertEx.Empty(sender.SentFailures);
    }

    [Test]
    public async Task FlushAsync_WithPendingItems_CallsSenderForEach()
    {
        var payloads = new[] { CreatePayload(), CreatePayload() };
        var store = Substitute.For<IDeadLetterStore>();
        store.GetPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<InvocationFailedPayload>>(payloads));
        var sender = new MockHubMessageSender();
        var service = CreateService(store, sender);

        await service.FlushAsync();

        AssertEx.Equal(2, sender.SentFailures.Count);
    }

    [Test]
    public async Task FlushAsync_WhenSendSucceeds_CallsRemoveAsync()
    {
        var payload = CreatePayload();
        var store = Substitute.For<IDeadLetterStore>();
        store.GetPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<InvocationFailedPayload>>([payload]));
        var service = CreateService(store, new MockHubMessageSender());

        await service.FlushAsync();

        await store.Received(1).RemoveAsync(payload.InvocationId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FlushAsync_WhenSendThrows_StopsProcessingRemainingItems()
    {
        var payloads = new[] { CreatePayload(), CreatePayload() };
        var store = Substitute.For<IDeadLetterStore>();
        store.GetPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<InvocationFailedPayload>>(payloads));
        var sender = new MockHubMessageSender();
        sender.ThrowOnNextSend(new InvalidOperationException("boom"));
        var service = CreateService(store, sender);

        await service.FlushAsync();

        AssertEx.Equal(0, sender.SentFailures.Count);
        await store.DidNotReceive().RemoveAsync(payloads[0].InvocationId, Arg.Any<CancellationToken>());
        await store.DidNotReceive().RemoveAsync(payloads[1].InvocationId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FlushAsync_WhenSendThrows_DoesNotCallRemoveAsync()
    {
        var payload = CreatePayload();
        var store = Substitute.For<IDeadLetterStore>();
        store.GetPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<InvocationFailedPayload>>([payload]));
        var sender = new MockHubMessageSender();
        sender.ThrowOnNextSend(new InvalidOperationException("boom"));
        var service = CreateService(store, sender);

        await service.FlushAsync();

        await store.DidNotReceive().RemoveAsync(payload.InvocationId, Arg.Any<CancellationToken>());
    }

    private static DeadLetterFlushService CreateService(IDeadLetterStore store, MockHubMessageSender sender)
    {
        return new DeadLetterFlushService(
            store,
            new Lazy<IHubMessageSender>(() => sender),
            NullLogger<DeadLetterFlushService>.Instance);
    }

    private static InvocationFailedPayload CreatePayload()
    {
        return new InvocationFailedPayload
        {
            InvocationId = Guid.NewGuid(),
            Error = "boom",
        };
    }
}
