namespace XE_Local_AI_Engine.Tests.Training;

using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Training;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     An operator cancel of a RUNNING generation. The executor owns the terminal write, and — unlike a host stop — it
///     must not rethrow, or the queue would log the operator's own cancel as a "queue failed" error and the next work
///     item would still be waiting behind a loop that took the shutdown path.
/// </summary>
public sealed class DatasetGenerationCancelTests
{
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(5);

    [Test]
    public async Task Cancel_OfARunningGeneration_CompletesAsCancelled_AndTheQueueKeepsRunning()
    {
        var datasetId = Guid.NewGuid();
        var cancellations = new TrainingRunCancellationRegistry();
        var store = Substitute.For<ITrainingDatasetStore>();
        _ = store.RecoverOnStartupAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<Guid>>([]);
        _ = store.GetDefinitionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Definition());

        var claims = 0;
        var polledAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = store.ClaimNextAsync(Arg.Any<CancellationToken>())
                 .Returns(_ =>
                 {
                     if (Interlocked.Increment(ref claims) == 1)
                     {
                         return Task.FromResult<DatasetGenerationClaimedWork?>(Work(datasetId));
                     }

                     // A second claim proves the loop survived the cancel instead of unwinding with it.
                     polledAgain.TrySetResult();
                     return Task.FromResult<DatasetGenerationClaimedWork?>(null);
                 });

        // The teacher turn is where a real cancel lands: the operator's request cancels the registered source, and the
        // in-flight turn observes it.
        var runner = Substitute.For<IStructuredAgentRunner>();
        _ = runner.RunAsync(Arg.Any<IChatClient>(), Arg.Any<StructuredAgentRequest>(), Arg.Any<CancellationToken>())
                  .Returns<Task<StructuredAgentResult>>(call =>
                  {
                      AssertEx.True(cancellations.Cancel(datasetId), "The executor must register the dataset before its first turn.");
                      throw new OperationCanceledException(call.Arg<CancellationToken>());
                  });

        using var signal = new DatasetGenerationQueueSignal();
        using var queue = new DatasetGenerationHostedService(ScopeFactory(store, runner, cancellations),
            signal,
            Substitute.For<IDatasetGenerationEventBuffer>(),
            new GpuWorkGate(),
            Options.Create(new DatasetGenerationQueueOptions()),
            NullLogger<DatasetGenerationHostedService>.Instance);

        await queue.StartAsync(CancellationToken.None);
        try
        {
            await polledAgain.Task.WaitAsync(BoundedWait);
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }

        _ = await store.Received(1)
                       .CompleteGenerationAsync(datasetId, DatasetGenerationWorkStatus.Cancelled, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static IServiceScopeFactory ScopeFactory(ITrainingDatasetStore store,
        IStructuredAgentRunner runner,
        TrainingRunCancellationRegistry cancellations)
    {
        var provider = Substitute.For<ILocalModelProvider>();
        _ = provider.ProviderName.Returns("llamacpp");
        _ = provider.CreateChatClient(Arg.Any<LocalModelSelection>()).Returns(_ => Substitute.For<IChatClient>());
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        _ = resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(provider);

        var services = new ServiceCollection();
        _ = services.AddLogging();
        _ = services.AddSingleton(cancellations);
        _ = services.AddScoped(_ => store);
        _ = services.AddScoped(_ => runner);
        _ = services.AddScoped(_ => resolver);
        _ = services.AddScoped(_ => Substitute.For<ISampleValidationPipeline>());
        _ = services.AddScoped(_ => Substitute.For<IDatasetGenerationEventBuffer>());
        _ = services.AddScoped<IDatasetGenerationExecutor, DatasetGenerationExecutor>();
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static TrainingDefinitionRecord Definition()
    {
        var body = new DatasetDefinitionBodyV1
        {
            TeacherModelName = "teacher",
            SystemInstructions = "produce examples",
            SampleKinds = [new DatasetSampleKindTargetV1("tool-call", 1, TrainingSampleLabel.Good)]
        };
        return new TrainingDefinitionRecord(Guid.NewGuid(),
            "definition",
            TrainingDatasetKind.ToolCalling,
            JsonSerializer.SerializeToUtf8Bytes(body, TrainingJson.Options),
            DefinitionVersion: 1,
            Version: 1,
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0);
    }

    private static DatasetGenerationClaimedWork Work(Guid datasetId) =>
        new(1,
            datasetId,
            1,
            new TrainingDatasetRecord(datasetId, Guid.NewGuid(), 1, "dataset", TrainingDatasetStatus.Generating, 1, null, 0, 0, 0, 0, 0, 1, 0, 0,
                DatasetGenerationWorkStatus.Running, null));
}
