namespace XE_Local_AI_Engine.Tests.Endpoints.LocalChat;

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ResolveToolApprovalEndpointTests
{
    private const string Route = "/api/local/v1/chat/approvals/resolve";

    [Test]
    public async Task ResolveApproval_WhenApproved_FeedsDispatchApprovalResolvedAndEchoesDecision()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        dispatcher.DispatchApprovalResolvedAsync(Arg.Any<ApprovalResolvedEvent>()).Returns(Task.CompletedTask);
        await using var factory = CreateFactory(dispatcher);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, new
        {
            requestId = "approval-xyz",
            approved = true
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await dispatcher.Received(1)
                        .DispatchApprovalResolvedAsync(Arg.Is<ApprovalResolvedEvent>(evt => evt.RequestId == "approval-xyz" && evt.Approved))
                        .ConfigureAwait(false);
    }

    [Test]
    public async Task ResolveApproval_WhenDenied_FeedsDispatchApprovalResolvedWithApprovedFalse()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        dispatcher.DispatchApprovalResolvedAsync(Arg.Any<ApprovalResolvedEvent>()).Returns(Task.CompletedTask);
        await using var factory = CreateFactory(dispatcher);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, new
        {
            requestId = "approval-den",
            approved = false
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await dispatcher.Received(1)
                        .DispatchApprovalResolvedAsync(Arg.Is<ApprovalResolvedEvent>(evt => evt.RequestId == "approval-den" && !evt.Approved))
                        .ConfigureAwait(false);
    }

    [Test]
    public async Task ResolveApproval_WhenBlankRequestId_RejectsAndNeverDispatches()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        await using var factory = CreateFactory(dispatcher);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, new
        {
            requestId = "",
            approved = true
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await dispatcher.DidNotReceive().DispatchApprovalResolvedAsync(Arg.Any<ApprovalResolvedEvent>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ResolveApproval_WhenMissingBearerToken_ReturnsUnauthorized()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        await using var factory = CreateFactory(dispatcher);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(new
            {
                requestId = "approval-xyz",
                approved = true
            })
        };
        request.Headers.Add("Origin", "http://localhost");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await dispatcher.DidNotReceive().DispatchApprovalResolvedAsync(Arg.Any<ApprovalResolvedEvent>()).ConfigureAwait(false);
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
