namespace XE_Local_AI_Engine.Tests.Testing;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Testing.FakeOpenAiGateway;
using XE_Local_AI_Engine.Testing.FakeOpenAiGateway.Determinism;

/// <summary>The test hosts' provider drives the fake gateway through the product's real OpenAI-compatible clients.</summary>
[Category(TestCategories.Integration)]
public sealed class FakeGatewayLocalModelProviderTests
{
    private static readonly LocalModelSelection ChatSelection = new()
    {
        ModelName = FakeGatewayLocalModelProvider.ChatModel,
        ProviderName = "llamacpp"
    };

    [Test]
    public async Task ChatClient_StreamsTheEchoedReply()
    {
        await using var gateway = await StartAsync();
        using var chat = new FakeGatewayLocalModelProvider(gateway).CreateChatClient(ChatSelection);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in chat.GetStreamingResponseAsync("say this back"))
        {
            updates.Add(update);
        }

        AssertEx.Equal($"{FakeGatewayLocalModelProvider.EchoPrefix} say this back", string.Concat(updates.Select(static update => update.Text)));
        AssertEx.Equal(FakeGatewayLocalModelProvider.ChatModel, gateway.RecordedRequests.Single().Model);
    }

    [Test]
    public async Task ChatClient_SurfacesAScriptedToolCallAsAFunctionCall()
    {
        await using var gateway = await StartAsync();
        gateway.State.ChatResponder = static (_, _) => Task.FromResult<FakeOpenAiGatewayScript?>(new FakeOpenAiGatewayScript
        {
            ToolCall = new FakeOpenAiGatewayToolCall
            {
                Name = "Calculate",
                Arguments = """{"expression":"12*9"}"""
            }
        });
        using var chat = new FakeGatewayLocalModelProvider(gateway).CreateChatClient(ChatSelection);

        var response = await chat.GetResponseAsync("what's 12*9?");

        var call = response.Messages.SelectMany(static message => message.Contents).OfType<FunctionCallContent>().Single();
        AssertEx.Equal("Calculate", call.Name);
        AssertEx.Equal("12*9", call.Arguments?["expression"]?.ToString());
    }

    [Test]
    public async Task EmbeddingGenerator_ReturnsTheDeterministicVector()
    {
        await using var gateway = await StartAsync();
        using var generator = new FakeGatewayLocalModelProvider(gateway).CreateEmbeddingGenerator(new LocalModelSelection
        {
            ModelName = FakeGatewayLocalModelProvider.EmbeddingModel,
            ProviderName = "llamacpp"
        });

        var embeddings = await generator.GenerateAsync(["stable input"]);

        var expected = EmbeddingDeterminism.EmbedDeterministic("stable input", dimensions: 384);
        var actual = embeddings.Single().Vector.ToArray();
        AssertEx.Equal(expected.Count, actual.Length);
        AssertEx.Equal((float)expected[0], actual[0]);
        AssertEx.Equal((float)expected[^1], actual[^1]);
    }

    [Test]
    public async Task ListModels_FollowsTheGatewayList_AndIsLlamaCppAndToolCapable()
    {
        await using var gateway = await StartAsync();
        var provider = new FakeGatewayLocalModelProvider(gateway);
        gateway.State.Models = [FakeGatewayLocalModelProvider.Model("added")];

        var models = await provider.ListModelsAsync(CancellationToken.None);

        var model = models.Single();
        AssertEx.Equal("llamacpp", provider.ProviderName);
        AssertEx.Equal("added", model.ModelName);
        AssertEx.Equal("llamacpp", model.ProviderName);
        AssertEx.True(model.IsToolCapable, "A listed model must be tool-capable, as the local-tools suites need.");
    }

    private static Task<FakeOpenAiGatewayServer> StartAsync() =>
        FakeOpenAiGatewayServer.StartAsync(FakeGatewayLocalModelProvider.DefaultOptions());
}
