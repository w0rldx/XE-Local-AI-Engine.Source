namespace XE_Local_AI_Engine.Tests.Endpoints.LocalChat;

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The cancel endpoint must not treat the base <c>InvalidOperationException</c> as "not found", or any unrelated
///     fault under <c>CancelMessageAsync</c> (a broken dependency, a bad invariant) reports as a missing message.
/// </summary>
/// <remarks>Real pipeline, persistence substituted: the typed correlation failure is 404, a plain one 500.</remarks>
[Category(TestCategories.Integration)]
public sealed class CancelNodeChatMessageEndpointTests
{
    private const string CancelRoute = "/api/local/v1/chat/cancel";

    [Test]
    public async Task Cancel_WhenTheServiceThrowsAnUnrelatedInvalidOperation_IsNoLongerReportedAsNotFound()
    {
        var response = await CancelAsync(new InvalidOperationException("the chat writer is misconfigured"));

        AssertEx.Equal(HttpStatusCode.InternalServerError, response,
            "an unrelated fault must reach the 500 handler instead of being flattened into 'the message is not there'.");
    }

    [Test]
    public async Task Cancel_WhenTheCorrelationNamesNoMessage_StillAnswersNotFound()
    {
        var response = await CancelAsync(new NodeChatMessageCorrelationNotFoundException("The correlated node chat message was not found."));

        AssertEx.Equal(HttpStatusCode.NotFound, response);
    }

    private static async Task<HttpStatusCode> CancelAsync(Exception thrownByCancel)
    {
        var persistence = Substitute.For<INodeChatPersistenceService>();
        persistence.CancelMessageAsync(Arg.Any<NodeChatCancelRequest>(), Arg.Any<CancellationToken>())
                   .Returns<Task<NodeChatCancelResultDto>>(_ => throw thrownByCancel);

        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services => services.AddSingleton(persistence)
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

        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }
}
