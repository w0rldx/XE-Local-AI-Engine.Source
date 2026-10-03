namespace XE_Local_AI_Engine.Tests.Endpoints.LocalChat;

using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The "Answer now" route over the real pipeline: 404 with no running turn, 409 when not reasoning or refused, 204
///     once the reasoning ended.
/// </summary>
/// <remarks>Only the llama-server client is substituted; the reasoning-control registry is the host's own singleton.</remarks>
[Category(TestCategories.Integration)]
public sealed class AnswerNowNodeChatMessageEndpointTests
{
    private static readonly Uri ServerBase = new("http://127.0.0.1:5811/v1");

    [Test]
    public async Task AnswerNow_ForAMessageWithNoRunningTurn_IsNotFound()
    {
        var native = Substitute.For<ILlamaServerNativeClient>();
        await using var factory = CreateFactory(native);

        AssertEx.Equal(HttpStatusCode.NotFound, await PostAsync(factory, Guid.NewGuid()));
    }

    [Test]
    public async Task AnswerNow_ForATurnThatIsNotReasoning_IsConflict()
    {
        var native = Substitute.For<ILlamaServerNativeClient>();
        await using var factory = CreateFactory(native);
        var messageId = Guid.NewGuid();
        using var turn = factory.Services.GetRequiredService<InvocationReasoningControl>().Track(messageId);
        turn.Observe(Advertised("chatcmpl-a"), sawReasoning: false, sawAnswer: true);

        AssertEx.Equal(HttpStatusCode.Conflict, await PostAsync(factory, messageId));
        await native.DidNotReceiveWithAnyArgs().EndReasoningAsync(default!, default!, default);
    }

    [Test]
    public async Task AnswerNow_WhenTheServerRefuses_IsConflict()
    {
        var native = Substitute.For<ILlamaServerNativeClient>();
        native.EndReasoningAsync(ServerBase, "chatcmpl-b", Arg.Any<CancellationToken>())
              .Returns(new LlamaServerReasoningControlResult
              {
                  Success = false,
                  Message = "control not armed for /srv/models/secret.gguf"
              });
        await using var factory = CreateFactory(native);
        var messageId = Guid.NewGuid();
        using var turn = factory.Services.GetRequiredService<InvocationReasoningControl>().Track(messageId);
        turn.Observe(Advertised("chatcmpl-b"), sawReasoning: true, sawAnswer: false);

        using var response = await SendAsync(factory, messageId);
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.False(body.Contains("secret.gguf", StringComparison.Ordinal), "the server's own text stays in the log, never in the response.");
    }

    [Test]
    public async Task AnswerNow_WhileTheArmedTurnReasons_EndsTheReasoning()
    {
        var native = Substitute.For<ILlamaServerNativeClient>();
        native.EndReasoningAsync(ServerBase, "chatcmpl-c", Arg.Any<CancellationToken>())
              .Returns(new LlamaServerReasoningControlResult
              {
                  Success = true
              });
        await using var factory = CreateFactory(native);
        var messageId = Guid.NewGuid();
        using var turn = factory.Services.GetRequiredService<InvocationReasoningControl>().Track(messageId);
        turn.Observe(Advertised("chatcmpl-c"), sawReasoning: true, sawAnswer: false);

        AssertEx.Equal(HttpStatusCode.NoContent, await PostAsync(factory, messageId));
        await native.Received(1).EndReasoningAsync(ServerBase, "chatcmpl-c", Arg.Any<CancellationToken>());
    }

    private static TestServerWebAppFactory CreateFactory(ILlamaServerNativeClient native) =>
        new()
        {
            ConfigureAdditionalTestServices = services => services.AddSingleton(native)
        };

    private static async Task<HttpStatusCode> PostAsync(TestServerWebAppFactory factory, Guid messageId)
    {
        using var response = await SendAsync(factory, messageId);
        return response.StatusCode;
    }

    private static async Task<HttpResponseMessage> SendAsync(TestServerWebAppFactory factory, Guid messageId)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/local/v1/chat/messages/{messageId}/answer-now");
        factory.AddNodeBearerToken(request);
        return await client.SendAsync(request);
    }

    private static ChatResponseUpdate Advertised(string completionId) =>
        new(ChatRole.Assistant, string.Empty)
        {
            ResponseId = completionId,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [LlamaServerReasoningControl.EndpointKey] = ServerBase
            }
        };
}
