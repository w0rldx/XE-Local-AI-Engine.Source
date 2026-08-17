namespace XE_Local_AI_Engine.Tests.Hubs;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Common.ProblemDetailModels.Enums;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The SignalR boundary's translation of the stream services' typed, caller-triggerable rejections. SignalR
///     forwards the MESSAGE of a <see cref="HubException" /> and of nothing else, so each rejection — thrown lazily
///     from inside the services' async iterators, long after the hub method returned its enumerable — otherwise
///     reaches the browser as the generic "An unexpected error occurred invoking 'SendMessage' on the server." These
///     pin the conversion, the discriminator token the SPA matches the read-only case on (which is what keeps the hub
///     and the REST 409 telling the user the same thing), and the narrowness that keeps every OTHER fault opaque.
/// </summary>
public sealed class LocalChatHubDomainRejectionTests
{
    private const string ExpectedPrefix = $"{nameof(NodeConflictProblemType.ReadOnlyConversation)}: ";

    [Test]
    public async Task SendMessage_WhenTheConversationIsReadOnly_SurfacesAHubExceptionCarryingTheConflictToken()
    {
        var conversationId = Guid.NewGuid();
        var streamService = Substitute.For<INodeChatStreamService>();
        streamService.SendMessageAsync(Arg.Any<NodeChatStreamRequest>(), Arg.Any<CancellationToken>())
                     .Returns(_ => ThrowsReadOnly(conversationId));

        using var hub = CreateHub(streamService, Substitute.For<INodeChatRegenerationService>());

        var exception = await AssertEx.ThrowsAsync<HubException>(async () =>
        {
            await foreach (var _ in hub.SendMessage(new NodeChatStreamRequest(conversationId, "hi"), CancellationToken.None).ConfigureAwait(false))
            {
                // The guard throws before the first event, so the body never runs.
            }
        });

        AssertReadOnlyMessage(exception, conversationId);
    }

    [Test]
    public async Task RegenerateMessage_WhenTheConversationIsReadOnly_SurfacesAHubExceptionCarryingTheConflictToken()
    {
        var conversationId = Guid.NewGuid();
        var regenerationService = Substitute.For<INodeChatRegenerationService>();
        regenerationService.RegenerateAsync(Arg.Any<Guid>(),
                               Arg.Any<Guid>(),
                               Arg.Any<string?>(),
                               Arg.Any<bool>(),
                               Arg.Any<bool>(),
                               Arg.Any<IReadOnlyDictionary<Guid, Guid>?>(),
                               Arg.Any<SamplingOptions?>(),
                               Arg.Any<CancellationToken>())
                           .Returns(_ => ThrowsReadOnly(conversationId));

        using var hub = CreateHub(Substitute.For<INodeChatStreamService>(), regenerationService);

        var exception = await AssertEx.ThrowsAsync<HubException>(async () =>
        {
            await foreach (var _ in hub.RegenerateMessage(conversationId,
                                           Guid.NewGuid(),
                                           reasoningEffort: null,
                                           useLocalTools: false,
                                           useKnowledgeBase: false,
                                           selectedPath: null,
                                           samplingOptions: null,
                                           CancellationToken.None)
                                       .ConfigureAwait(false))
            {
                // The guard throws before the first event, so the body never runs.
            }
        });

        AssertReadOnlyMessage(exception, conversationId);
    }

    [Test]
    public async Task RegenerateMessage_WhenTheConversationIsGone_SurfacesTheSentenceInsteadOfSignalRsGenericError()
    {
        // Same failure mode the read-only case fixed, for the outcomes a stale tab actually hits: a deleted
        // conversation, a deleted message, a correlation already generating. Untranslated they reach the browser as
        // "An unexpected error occurred invoking 'RegenerateMessage' on the server." and the operator learns nothing.
        var conversationId = Guid.NewGuid();

        var exception = await RegenerateThrowingAsync(new NodeChatConversationNotFoundException(conversationId)).ConfigureAwait(false);

        AssertEx.Equal(new NodeChatConversationNotFoundException(conversationId).Message, exception.Message);
    }

    [Test]
    public async Task RegenerateMessage_WhenTheMessageIsGone_SurfacesTheSentenceInsteadOfSignalRsGenericError()
    {
        var messageId = Guid.NewGuid();

        var exception = await RegenerateThrowingAsync(new NodeChatMessageNotFoundException(messageId)).ConfigureAwait(false);

        AssertEx.Equal(new NodeChatMessageNotFoundException(messageId).Message, exception.Message);
    }

    [Test]
    public async Task RegenerateMessage_WhenTheCorrelationIsAlreadyStreaming_SurfacesTheSentenceInsteadOfSignalRsGenericError()
    {
        var exception = await RegenerateThrowingAsync(new NodeChatStreamAlreadyActiveException()).ConfigureAwait(false);

        AssertEx.Equal(new NodeChatStreamAlreadyActiveException().Message, exception.Message);
    }

    [Test]
    public async Task SendMessage_WhenTheStreamFailsForAnyOtherReason_LeavesTheExceptionAlone()
    {
        // The conversion must be narrow: turning every fault into a HubException would forward internal detail to the
        // browser, which is exactly what SignalR's generic message exists to prevent.
        var streamService = Substitute.For<INodeChatStreamService>();
        streamService.SendMessageAsync(Arg.Any<NodeChatStreamRequest>(), Arg.Any<CancellationToken>())
                     .Returns(_ => ThrowsAsync(new InvalidOperationException("model unavailable")));

        using var hub = CreateHub(streamService, Substitute.For<INodeChatRegenerationService>());

        var exception = await AssertEx.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in hub.SendMessage(new NodeChatStreamRequest(Guid.NewGuid(), "hi"), CancellationToken.None).ConfigureAwait(false))
            {
                // Nothing is ever yielded.
            }
        });

        AssertEx.Equal("model unavailable", exception.Message);
    }

    private static async Task<HubException> RegenerateThrowingAsync(Exception rejection)
    {
        var regenerationService = Substitute.For<INodeChatRegenerationService>();
        regenerationService.RegenerateAsync(Arg.Any<Guid>(),
                               Arg.Any<Guid>(),
                               Arg.Any<string?>(),
                               Arg.Any<bool>(),
                               Arg.Any<bool>(),
                               Arg.Any<IReadOnlyDictionary<Guid, Guid>?>(),
                               Arg.Any<SamplingOptions?>(),
                               Arg.Any<CancellationToken>())
                           .Returns(_ => ThrowsAsync(rejection));

        using var hub = CreateHub(Substitute.For<INodeChatStreamService>(), regenerationService);

        return await AssertEx.ThrowsAsync<HubException>(async () =>
        {
            await foreach (var _ in hub.RegenerateMessage(Guid.NewGuid(),
                                           Guid.NewGuid(),
                                           reasoningEffort: null,
                                           useLocalTools: false,
                                           useKnowledgeBase: false,
                                           selectedPath: null,
                                           samplingOptions: null,
                                           CancellationToken.None)
                                       .ConfigureAwait(false))
            {
                // The rejection is thrown before the first event, so the body never runs.
            }
        }).ConfigureAwait(false);
    }

    private static void AssertReadOnlyMessage(HubException exception, Guid conversationId)
    {
        AssertEx.True(exception.Message.StartsWith(ExpectedPrefix, StringComparison.Ordinal),
            $"the SPA discriminates on the leading conflict token, but the message was '{exception.Message}'");

        // The original sentence must survive intact — it is what the REST 409's `detail` carries.
        AssertEx.Contains(exception.Message, new NodeChatReadOnlyConversationException(conversationId).Message);
    }

    private static LocalChatHub CreateHub(INodeChatStreamService streamService, INodeChatRegenerationService regenerationService)
    {
        return new LocalChatHub(streamService,
            regenerationService,
            Substitute.For<IInvocationResumeRegistry>(),
            Substitute.For<IInvocationAttachmentTracker>(),
            Options.Create(new SecurityOptions()));
    }

    // Mirrors the real services: the guard runs INSIDE the async iterator, so the exception surfaces on the first
    // MoveNextAsync rather than from the call that built the enumerable.
    private static async IAsyncEnumerable<ChatStreamEvent> ThrowsReadOnly(Guid conversationId)
    {
        await Task.Yield();
        throw new NodeChatReadOnlyConversationException(conversationId);
#pragma warning disable CS0162 // Unreachable: required to make this an iterator rather than a plain async method.
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<ChatStreamEvent> ThrowsAsync(Exception exception)
    {
        await Task.Yield();
        throw exception;
#pragma warning disable CS0162 // Unreachable: required to make this an iterator rather than a plain async method.
        yield break;
#pragma warning restore CS0162
    }
}
