namespace XE_Local_AI_Engine.Tests.Hubs;

using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
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
