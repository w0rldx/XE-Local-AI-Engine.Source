namespace XE_Local_AI_Engine.Tests.Hubs;

using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <c>StreamDirectInference</c> end to end through the real hub, the real service and the test host's fake Ollama chat
///     client: the browser's wire, the operator gate and the eager refusal.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class LocalChatHubDirectInferenceTests
{
    private const string Method = "StreamDirectInference";

    [Test]
    public async Task StreamDirectInference_WhenAuthorized_StreamsDeltasThenUsageThenDone()
    {
        await using var factory = new TestServerWebAppFactory();
        await using var connection = CreateHubConnection(factory, authorized: true);
        await connection.StartAsync();

        var events = await connection.StreamAsync<DirectInferenceEvent>(Method,
                                         new DirectInferenceRequest { ModelId = "qwen3.5:0.8b", SystemPrompt = "Be terse.", Prompt = "hello" })
                                     .ToListAsync();

        AssertEx.True(events.Count >= 3, $"expected deltas, usage and done; got {string.Join(",", events.Select(static e => e.Type))}");
        AssertEx.True(events.Any(static e => e.Type == DirectInferenceEventTypes.Delta && !string.IsNullOrEmpty(e.TextDelta)));
        var usage = AssertEx.NotNull(events.Single(static e => e.Type == DirectInferenceEventTypes.Usage).Usage);
        AssertEx.Equal(5L, usage.InputTokens, "the fake counts the user message's characters");
        AssertEx.Empty(events.Where(static e => e.Type == DirectInferenceEventTypes.Timings), "an Ollama answer carries no llama-server timings");
        var done = events[^1];
        AssertEx.Equal(DirectInferenceEventTypes.Done, done.Type);
        AssertEx.Equal("stop", done.FinishReason);
        AssertEx.True(done.ElapsedMs is not null);
    }

    [Test]
    public async Task StreamDirectInference_AnOutOfRangeSamplingValue_IsRefusedWithAHubException()
    {
        await using var factory = new TestServerWebAppFactory();
        await using var connection = CreateHubConnection(factory, authorized: true);
        await connection.StartAsync();

        var exception = await AssertEx.ThrowsAsync<HubException>(async () =>
            await connection.StreamAsync<DirectInferenceEvent>(Method,
                                new DirectInferenceRequest
                                {
                                    ModelId = "qwen3.5:0.8b",
                                    Prompt = "hello",
                                    SamplingOptions = new SamplingOptions { TopP = 1.5f }
                                })
                            .ToListAsync());

        AssertEx.Contains(exception.Message, "Top P must be between 0 and 1.");
    }

    [Test]
    public async Task StreamDirectInference_ALogprobsEvent_CrossesTheWireIntact()
    {
        // The fake chat client is no llama.cpp model, so the service is the seam: this test is about the hub's JSON protocol.
        var service = Substitute.For<IDirectInferenceService>();
        service.StreamAsync(Arg.Any<DirectInferenceRequest>(), Arg.Any<CancellationToken>())
               .Returns(new[]
                   {
                       new DirectInferenceEvent
                       {
                           Type = DirectInferenceEventTypes.Logprobs,
                           Logprobs =
                           [
                               new DirectInferenceLogprobEntry
                               {
                                   Text = "🌏",
                                   Logprob = -0.5f,
                                   IsReasoning = true,
                                   Alternatives = [new DirectInferenceLogprobAlternative { Text = "�", Logprob = -float.MaxValue }]
                               }
                           ]
                       }
                   }.ToAsyncEnumerable());
        await using var factory = new TestServerWebAppFactory { ConfigureAdditionalTestServices = services => services.AddSingleton(service) };
        await using var connection = CreateHubConnection(factory, authorized: true);
        await connection.StartAsync();

        var received = await connection.StreamAsync<DirectInferenceEvent>(Method, new DirectInferenceRequest { ModelId = "m", Prompt = "hello", TopLogprobs = 1 }).ToListAsync();

        var entry = AssertEx.NotNull(received.Single().Logprobs).Single();
        AssertEx.Equal("🌏", entry.Text);
        AssertEx.Equal(-0.5f, entry.Logprob);
        AssertEx.True(entry.IsReasoning);
        AssertEx.Equal(-float.MaxValue, entry.Alternatives.Single().Logprob, "llama-server's stand-in for a zero probability survives the protocol");
        AssertEx.Equal(1, service.ReceivedCalls().Single().GetArguments().OfType<DirectInferenceRequest>().Single().TopLogprobs);
    }

    [Test]
    public async Task StreamDirectInference_WithoutAToken_CannotConnect()
    {
        await using var factory = new TestServerWebAppFactory();
        await using var connection = CreateHubConnection(factory, authorized: false);

        var exception = await AssertEx.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());

        AssertEx.Contains(exception.Message, "401");
    }

    private static HubConnection CreateHubConnection(TestServerWebAppFactory factory, bool authorized)
    {
        return new HubConnectionBuilder()
               .WithUrl("http://localhost" + LocalApiRoutes.LocalChat.Hub, options =>
               {
                   options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                   if (authorized)
                   {
                       options.AccessTokenProvider = () => Task.FromResult<string?>(factory.CreateNodeAccessToken());
                   }

                   options.Headers.Add("Origin", "http://localhost");
               })
               .WithNodeJsonProtocol()
               .Build();
    }
}
