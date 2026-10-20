namespace XE_Local_AI_Engine.Tests.Knowledge;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class KnowledgeRerankerStartupWarmServiceTests
{
    private readonly INodeRuntimeSettings _runtimeSettings = Substitute.For<INodeRuntimeSettings>();
    private readonly IKnowledgeModelPrewarmer _prewarmer = Substitute.For<IKnowledgeModelPrewarmer>();

    [Test]
    public async Task Start_WithARerankerConfigured_RequestsOneWarm()
    {
        _runtimeSettings.GetRerankerModelNameAsync(Arg.Any<CancellationToken>()).Returns("bge-reranker-v2-m3");

        await RunToCompletionAsync();

        _prewarmer.Received(1).RequestWarm();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("  ")]
    public async Task Start_WithNoRerankerConfigured_RequestsNoWarm(string? rerankerModelName)
    {
        _runtimeSettings.GetRerankerModelNameAsync(Arg.Any<CancellationToken>()).Returns(rerankerModelName);

        await RunToCompletionAsync();

        _prewarmer.DidNotReceive().RequestWarm();
    }

    [Test]
    public async Task Start_WhenTheSettingReadFails_CompletesWithoutAWarm()
    {
        // A throw out of ExecuteAsync would stop the host (StopHost), so a failed read must be swallowed and logged.
        _runtimeSettings.GetRerankerModelNameAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("settings unreadable"));

        await RunToCompletionAsync();

        _prewarmer.DidNotReceive().RequestWarm();
    }

    private async Task RunToCompletionAsync()
    {
        using var service = new KnowledgeRerankerStartupWarmService(_runtimeSettings,
            _prewarmer,
            NullLogger<KnowledgeRerankerStartupWarmService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await AssertEx.NotNull(service.ExecuteTask).WaitAsync(TimeSpan.FromSeconds(30));
    }
}
