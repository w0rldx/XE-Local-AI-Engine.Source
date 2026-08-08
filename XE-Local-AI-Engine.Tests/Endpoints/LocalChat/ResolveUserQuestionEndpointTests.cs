namespace XE_Local_AI_Engine.Tests.Endpoints.LocalChat;

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ResolveUserQuestionEndpointTests
{
    private const string Route = "/api/local/v1/chat/questions/resolve";

    [Test]
    public async Task ResolveQuestion_WhenAnswered_FeedsDispatchUserQuestionAnswered()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        dispatcher.DispatchUserQuestionAnsweredAsync(Arg.Any<UserQuestionAnsweredEvent>()).Returns(Task.CompletedTask);
        await using var factory = CreateFactory(dispatcher);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, new
        {
            requestId = "question-xyz",
            answers = new[]
            {
                new
                {
                    question = "Which auth method?",
                    selected = new[]
                    {
                        "OAuth"
                    },
                    other = (string?)null
                }
            }
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await dispatcher.Received(1)
                        .DispatchUserQuestionAnsweredAsync(Arg.Is<UserQuestionAnsweredEvent>(evt => evt.RequestId == "question-xyz"
                                                                                                    && evt.Answers.Count == 1
                                                                                                    && evt.Answers[0].Question == "Which auth method?"
                                                                                                    && evt.Answers[0].Selected.Count == 1
                                                                                                    && evt.Answers[0].Selected[0] == "OAuth"
                                                                                                    && evt.Answers[0].Other == null))
                        .ConfigureAwait(false);
    }

    [Test]
    public async Task ResolveQuestion_WhenOnlyFreeText_IsAcceptedWithAnEmptySelection()
    {
        // "Other" alone is a complete answer: the client appends that row, the model never declares it.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        dispatcher.DispatchUserQuestionAnsweredAsync(Arg.Any<UserQuestionAnsweredEvent>()).Returns(Task.CompletedTask);
        await using var factory = CreateFactory(dispatcher);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, new
        {
            requestId = "question-other",
            answers = new[]
            {
                new
                {
                    question = "Which auth method?",
                    other = "mTLS"
                }
            }
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await dispatcher.Received(1)
                        .DispatchUserQuestionAnsweredAsync(Arg.Is<UserQuestionAnsweredEvent>(evt => evt.Answers[0].Selected.Count == 0
                                                                                                    && evt.Answers[0].Other == "mTLS"))
                        .ConfigureAwait(false);
    }

    [Test]
    public async Task ResolveQuestion_WhenStaleRequestId_StillSucceedsBecauseTheDispatchIsIdempotent()
    {
        // An unknown or already-resolved id must never fault: a duplicate submit, or an answer that lost the race
        // with a timeout, just does nothing.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        dispatcher.DispatchUserQuestionAnsweredAsync(Arg.Any<UserQuestionAnsweredEvent>()).Returns(Task.CompletedTask);
        await using var factory = CreateFactory(dispatcher);
        using var client = factory.CreateClient();

        using var first = CreateRequest(factory, NewBody("question-dupe"));
        using var firstResponse = await client.SendAsync(first).ConfigureAwait(false);
        using var second = CreateRequest(factory, NewBody("question-dupe"));
        using var secondResponse = await client.SendAsync(second).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        await dispatcher.Received(2).DispatchUserQuestionAnsweredAsync(Arg.Any<UserQuestionAnsweredEvent>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ResolveQuestion_WhenBlankRequestId_RejectsAndNeverDispatches()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        await using var factory = CreateFactory(dispatcher);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, NewBody(string.Empty));
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await dispatcher.DidNotReceive().DispatchUserQuestionAnsweredAsync(Arg.Any<UserQuestionAnsweredEvent>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ResolveQuestion_WhenAnAnswerCarriesNeitherSelectionNorText_RejectsAndNeverDispatches()
    {
        // A content-free answer would release the turn on nothing the model can branch on.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        await using var factory = CreateFactory(dispatcher);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, new
        {
            requestId = "question-empty",
            answers = new[]
            {
                new
                {
                    question = "Which auth method?",
                    selected = Array.Empty<string>(),
                    other = (string?)null
                }
            }
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await dispatcher.DidNotReceive().DispatchUserQuestionAnsweredAsync(Arg.Any<UserQuestionAnsweredEvent>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ResolveQuestion_WhenNoAnswers_RejectsAndNeverDispatches()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        await using var factory = CreateFactory(dispatcher);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, new
        {
            requestId = "question-none",
            answers = Array.Empty<object>()
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await dispatcher.DidNotReceive().DispatchUserQuestionAnsweredAsync(Arg.Any<UserQuestionAnsweredEvent>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ResolveQuestion_WhenMissingBearerToken_ReturnsUnauthorized()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        await using var factory = CreateFactory(dispatcher);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(NewBody("question-xyz"))
        };
        request.Headers.Add("Origin", "http://localhost");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await dispatcher.DidNotReceive().DispatchUserQuestionAnsweredAsync(Arg.Any<UserQuestionAnsweredEvent>()).ConfigureAwait(false);
    }

    private static object NewBody(string requestId)
    {
        return new
        {
            requestId,
            answers = new[]
            {
                new
                {
                    question = "Which auth method?",
                    selected = new[]
                    {
                        "OAuth"
                    }
                }
            }
        };
    }

    private static TestingWebAppFactory CreateFactory(IWorkerEventDispatcher dispatcher)
    {
        return new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IWorkerEventDispatcher>();
                services.AddSingleton(dispatcher);
            }
        };
    }

    private static HttpRequestMessage CreateRequest(TestingWebAppFactory factory, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(body)
        };
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }
}
