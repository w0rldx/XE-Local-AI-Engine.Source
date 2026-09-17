namespace XE_Local_AI_Engine.Tests.Endpoints.LocalChat;

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The cancel endpoint used to catch the base <c>InvalidOperationException</c> as its "not found" signal, so any
///     unrelated fault raised under <c>CancelMessageAsync</c> — a broken dependency, a bad invariant — was reported to
///     the operator as a missing message. These drive the real pipeline with the persistence service substituted, and
///     assert both halves of the narrowing: the typed correlation failure is still a 404, and a plain
///     <c>InvalidOperationException</c> is now the 500 that says something is broken.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class CancelNodeChatMessageEndpointTests
{
    private const string CancelRoute = "/api/local/v1/chat/cancel";

    [Test]
    public async Task Cancel_WhenTheServiceThrowsAnUnrelatedInvalidOperation_IsNoLongerReportedAsNotFound()
    {
        var response = await CancelAsync(new InvalidOperationException("the chat writer is misconfigured")).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.InternalServerError, response,
            "an unrelated fault must reach the 500 handler instead of being flattened into 'the message is not there'.");
    }

    [Test]
    public async Task Cancel_WhenTheCorrelationNamesNoMessage_StillAnswersNotFound()
    {
        var response = await CancelAsync(new NodeChatMessageCorrelationNotFoundException("The correlated node chat message was not found."))
            .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response);
    }

    private static async Task<HttpStatusCode> CancelAsync(Exception thrownByCancel)
    {
        var persistence = Substitute.For<INodeChatPersistenceService>();
        persistence.CancelMessageAsync(Arg.Any<NodeChatCancelRequest>(), Arg.Any<CancellationToken>())
                   .Returns<Task<NodeChatCancelResultDto>>(_ => throw thrownByCancel);

        var mutationGuard = Substitute.For<INodeChatMutationGuard>();
        mutationGuard.EnsureMutableAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.AddSingleton(persistence);
                services.AddSingleton(mutationGuard);
            }
        };
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, CancelRoute)
        {
            Content = JsonContent.Create(new
            {
                conversationId = Guid.NewGuid(),
                messageId = Guid.NewGuid(),
                requestId = Guid.NewGuid()
            })
        };
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        return response.StatusCode;
    }
}
