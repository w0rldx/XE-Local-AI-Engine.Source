namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using TUnit.Core.Interfaces;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The agent-lane host with the <c>/v1/systemone</c> client faked and two installed GGUFs: one whose header declares
///     a decision type, one plain chat model. Everything between the node and the client is real.
/// </summary>
public sealed class GraphWorkflowSystemOneHostFixture : IAsyncInitializer, IAsyncDisposable
{
    /// <summary>An installed GGUF whose header carries <c>modern-bert.decision.type = laya</c>.</summary>
    public const string DecisionModel = "ggml-org/Julia-1-GGUF:Q8_0";

    /// <summary>An installed chat GGUF with no decision type.</summary>
    public const string ChatModel = "unsloth/Qwen3.5-4B-GGUF:Q4_K_M";

    public TestServerWebAppFactory Factory { get; } = GraphWorkflowAgentHostFixture.NewFactory(static services =>
    {
        var ggufModels = Substitute.For<IGgufModelStore>();
        ggufModels.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        ggufModels.ListInstalledModelsAsync(Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<IReadOnlyList<LocalModelDescriptor>>([Installed(DecisionModel, "laya"), Installed(ChatModel, decisionType: null)]));
        services.RemoveAll<IGgufModelStore>();
        services.AddSingleton(ggufModels);

        services.RemoveAll<ILlamaServerSystemOneClient>();
        services.AddSingleton<FakeSystemOneClient>();
        services.AddSingleton<ILlamaServerSystemOneClient>(provider => provider.GetRequiredService<FakeSystemOneClient>());
    });

    internal FakeSystemOneClient Client => Factory.Services.GetRequiredService<FakeSystemOneClient>();

    public Task InitializeAsync() =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() =>
        Factory.DisposeAsync();

    private static LocalModelDescriptor Installed(string modelName, string? decisionType) =>
        new()
        {
            ModelName = modelName,
            ProviderName = "llamacpp",
            IsAvailable = true,
            SizeBytes = 1,
            ModifiedAt = DateTimeOffset.UnixEpoch,
            MaxContextTokens = 8192,
            DecisionType = decisionType
        };
}
