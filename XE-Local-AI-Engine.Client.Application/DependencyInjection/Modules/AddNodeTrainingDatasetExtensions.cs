namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Runs;

internal static class AddNodeTrainingDatasetExtensions
{
    public static IHostApplicationBuilder AddNodeTrainingDatasets(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddScoped<ITrainingDatasetStore, TrainingDatasetStore>();

        // The node's GPU-work admission gate (decision #13). Singleton: it is the whole point that every consumer sees
        // the same one. TryAdd so the run module can register it too without a duplicate winning.
        builder.Services.TryAddSingleton<IGpuWorkGate, GpuWorkGate>();

        // Shared with the run module: the generation executor registers its live cancellation under the DATASET id,
        // which is never a run id. TryAdd for the same reason as the gate.
        builder.Services.TryAddSingleton<TrainingRunCancellationRegistry>();

        builder.Services.AddScoped<IDatasetDefinitionService, DatasetDefinitionService>();
        builder.Services.AddScoped<IDatasetGenerationService, DatasetGenerationService>();
        builder.Services.AddScoped<IDatasetGenerationExecutor, DatasetGenerationExecutor>();
        builder.Services.AddScoped<IDatasetExportService, DatasetExportService>();
        builder.Services.AddScoped<ISampleValidationPipeline, SampleValidationPipeline>();
        builder.Services.AddScoped<IHeadlessToolExecutor, HeadlessToolExecutor>();
        builder.Services.AddScoped<IToolMockService, ToolMockService>();
        // Scoped, not singleton: the runner consumes the scoped IModelCapabilityResolver for its reasoning-mode gate.
        builder.Services.AddScoped<IStructuredAgentRunner, StructuredAgentRunner>();
        builder.Services.AddSingleton<IToolMockEngine, ToolMockEngine>();
        builder.Services.AddSingleton<IToolMockStaticVerifier, ToolMockStaticVerifier>();
        builder.Services.AddOptions<DatasetGenerationEventBufferOptions>();
        builder.Services.AddOptions<DatasetGenerationQueueOptions>();
        builder.Services.AddSingleton<IDatasetGenerationEventBuffer, DatasetGenerationEventBuffer>();
        builder.Services.AddSingleton<DatasetGenerationQueueSignal>();
        builder.Services.AddSingleton<IDatasetGenerationQueueSignal>(provider => provider.GetRequiredService<DatasetGenerationQueueSignal>());
        builder.Services.AddHostedService<DatasetGenerationHostedService>();
        return builder;
    }
}
