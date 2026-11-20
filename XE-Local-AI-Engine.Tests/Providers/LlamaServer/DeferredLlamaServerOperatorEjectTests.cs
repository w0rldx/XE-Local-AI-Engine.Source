namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A background chat client of an operator-ejected model surfaces the supervisor's <see cref="OperatorEjectedException" />
///     unwrapped, through both entry points, so the background workers can recognise the refusal as a skip.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class DeferredLlamaServerOperatorEjectTests
{
    private const string Model = "model-a";

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BackgroundClient_OfAnEjectedModel_ThrowsTheOperatorEjectedTypeUnwrapped(bool streaming)
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);
        await supervisor.EjectAsync(Model, ModelRole.Chat, force: false, CancellationToken.None, operatorRequested: true);
        // The client a background worker gets: built by the provider from a Background selection.
        using var client = new LlamaServerLocalModelProvider(supervisor, new FakeModelStore(), TimeProvider.System).CreateChatClient(new LocalModelSelection
        {
            ModelName = Model,
            ProviderName = "llamacpp",
            ResidencyIntent = ModelResidencyIntent.Background
        });
        ChatMessage[] messages = [new(ChatRole.User, "hello")];

        var exception = await AssertEx.ThrowsAsync<Exception>(async () =>
        {
            if (streaming)
            {
                await foreach (var update in client.GetStreamingResponseAsync(messages))
                {
                    AssertEx.Null(update, "No update may stream from a refused load.");
                }
            }
            else
            {
                await client.GetResponseAsync(messages);
            }
        });

        AssertEx.Equal(typeof(OperatorEjectedException), exception.GetType(), exception.ToString());
        AssertEx.Equal(expected: 0, launcher.LaunchCount);
    }
}
