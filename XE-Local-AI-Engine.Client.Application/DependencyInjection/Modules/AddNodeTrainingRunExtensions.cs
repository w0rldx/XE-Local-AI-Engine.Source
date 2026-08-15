namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Services.Training;
using XE_Local_AI_Engine.Client.Services.Training.Runs;

/// <summary>
///     Registers the training run queue: admission, the single-consumer executor, and the startup reaper.
/// </summary>
/// <remarks>
///     <strong>Caller contract:</strong> invoke after <c>AddNodeTrainingRuntime</c> (which registers
///     <c>ITrainingRunStore</c> and the process spawner) and after the llama.cpp runtime module, whose supervisor
///     provides the runtime-mutation lease the queue acquires before every claim. The reaper is registered BEFORE the
///     queue so its receipt validation runs against receipts the queue's own recovery has not yet cleared.
/// </remarks>
internal static class AddNodeTrainingRunExtensions
{
    public static IHostApplicationBuilder AddNodeTrainingRuns(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Also registered by the dataset module; TryAdd so whichever composes first wins and both see the same flag.
        builder.Services.TryAddSingleton<ITrainingActivity, TrainingActivity>();

        builder.Services.AddOptions<TrainingRunEventBufferOptions>();
        builder.Services.AddOptions<TrainingRunQueueOptions>();
        builder.Services.AddSingleton<ITrainingRunEventBuffer, TrainingRunEventBuffer>();
        builder.Services.AddSingleton<TrainingRunQueueSignal>();
        builder.Services.AddSingleton<ITrainingRunQueueSignal>(provider => provider.GetRequiredService<TrainingRunQueueSignal>());

        // Singletons: the registry outlives the executor's scope (cancel arrives on a request scope), and the
        // workspace is a pure path/crypto helper with no per-request state.
        builder.Services.AddSingleton<TrainingRunCancellationRegistry>();
        builder.Services.AddSingleton<TrainingRunWorkspace>();

        builder.Services.AddScoped<ITrainingOptionDefaultsCalculator, TrainingOptionDefaultsCalculator>();
        builder.Services.AddScoped<ILicenseGateService, LicenseGateService>();
        builder.Services.AddScoped<ITrainingCapacityGate, TrainingCapacityGate>();
        builder.Services.AddScoped<ITrainingRunService, TrainingRunService>();
        builder.Services.AddScoped<ITrainingRunExecutor, TrainingRunExecutor>();

        builder.Services.AddHostedService<TrainingRunStartupReaper>();
        builder.Services.AddHostedService<TrainingRunQueueHostedService>();
        return builder;
    }
}
