namespace XE_Local_AI_Engine.Tests.Providers.OpenAICompatible;

using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.OpenAICompatible.Core;
using XE_Local_AI_Engine.Tests.Providers.OpenAICompat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The shared factory's auth branch decides what a remote server sees. These tests assert the FINAL wire headers
///     through the real assembled System.ClientModel pipeline (the agent-knowledge §4 pattern) rather than inspecting
///     options, because the SDK's own placeholder-credential policy runs inside that pipeline and is exactly what a
///     naive "no credential" implementation would silently lose to.
/// </summary>
public sealed class OpenAICompatibleClientFactoryTests
{
    private static readonly Uri BaseAddress = new("http://127.0.0.1:1/v1/");

    [Test]
    public async Task CreateChatClient_WithAnApiKey_SendsABearerAuthorizationHeader()
    {
        var recorder = new OpenAiWireRecorder();
        using var chat = BuildChatClient(recorder, apiKey: "sk-unsloth-secret");

        _ = await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);

        AssertEx.Equal("Bearer sk-unsloth-secret", recorder.LastRequest.Authorization);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task CreateChatClient_WithoutAnApiKey_SendsNoAuthorizationHeaderAtAll(string? apiKey)
    {
        // NOT an empty or sentinel bearer: an endpoint that validates the header it is sent would reject a bogus one,
        // and the value would land in that server's access logs. Absent means absent.
        var recorder = new OpenAiWireRecorder();
        using var chat = BuildChatClient(recorder, apiKey);

        _ = await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);

        AssertEx.False(recorder.LastRequest.HasAuthorizationHeader, "a keyless connection must send no Authorization header.");
        AssertEx.Null(recorder.LastRequest.Authorization);
    }

    [Test]
    public void BuildClientOptions_PinsTheEndpointNetworkTimeoutAndAnExplicitRetryPolicy()
    {
        var options = OpenAICompatibleClientFactory.BuildClientOptions(BaseAddress, TimeSpan.FromSeconds(600));

        AssertEx.Equal(BaseAddress, options.Endpoint);
        // Never the SDK's 100 s default: a long local generation must not be aborted mid-answer.
        AssertEx.Equal(TimeSpan.FromSeconds(600), options.NetworkTimeout);
        // Explicitly set (ClientRetryPolicy does not expose its count); the no-retry behavior itself is asserted below.
        AssertEx.NotNull(options.RetryPolicy);
    }

    [Test]
    public void BuildClientOptions_WithANonPositiveTimeout_FallsBackToInfiniteRatherThanThrowing()
    {
        var options = OpenAICompatibleClientFactory.BuildClientOptions(BaseAddress, TimeSpan.Zero);

        AssertEx.Equal(Timeout.InfiniteTimeSpan, options.NetworkTimeout);
    }

    [Test]
    public async Task CreatedClient_DoesNotRetry_OnARetryableFailure()
    {
        // A chat completion is non-idempotent: a default retry policy would silently produce a second generation. 503 is
        // classically retryable, so exactly one transport hit proves the pinned ClientRetryPolicy(0) survived assembly.
        var recorder = new OpenAiWireRecorder
        {
            Responder = static _ => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{}")
            }
        };
        using var chat = BuildChatClient(recorder, apiKey: "k");

        _ = await AssertEx.ThrowsAsync<System.ClientModel.ClientResultException>(() =>
            chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None));

        AssertEx.Equal(1, recorder.Requests.Count);
    }

    private static IChatClient BuildChatClient(OpenAiWireRecorder recorder, string? apiKey)
    {
#pragma warning disable CA2000 // The handler, HttpClient and transport all transfer to the returned chat client, which owns them.
        var httpClient = new HttpClient(recorder.CreateHandler(), disposeHandler: true);
        return OpenAICompatibleClientFactory.CreateChatClient(BaseAddress,
            "test-model",
            apiKey,
            TimeSpan.FromSeconds(30),
            new HttpClientPipelineTransport(httpClient));
#pragma warning restore CA2000
    }
}
