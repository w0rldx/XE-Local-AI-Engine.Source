namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Benchmarks.PythonTests;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;

internal static class AddNodeBenchmarksExtensions
{
    public static IHostApplicationBuilder AddNodeBenchmarks(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddScoped<IBenchmarkStore, BenchmarkStore>();
        builder.Services.AddScoped<IBenchmarkInstalledModelLeaseProvider, BenchmarkInstalledModelLeaseProvider>();
        builder.Services.AddScoped<IBenchmarkProjectService, BenchmarkProjectService>();
        builder.Services.AddScoped<IBenchmarkTaskItemService, BenchmarkTaskItemService>();
        builder.Services.AddScoped<IBenchmarkExportFactsResolver, BenchmarkExportFactsResolver>();
        builder.Services.AddScoped<IBenchmarkExportQuery, BenchmarkExportQuery>();
        builder.Services.AddScoped<IBenchmarkFreezeDependencyService, BenchmarkFreezeDependencyService>();
        builder.Services.AddScoped<IBenchmarkPhaseLaunchResolver, BenchmarkPhaseLaunchResolver>();
        builder.Services.AddScoped<IBenchmarkJudgeRuntimeResolver, BenchmarkJudgeRuntimeResolver>();
        builder.Services.AddScoped<IBenchmarkRunFreezeService, BenchmarkRunFreezeService>();
        builder.Services.AddScoped<IBenchmarkRunBatchService, BenchmarkRunBatchService>();
        builder.Services.AddScoped<IBenchmarkCatalogService, BenchmarkCatalogService>();
        builder.Services.AddSingleton<IBenchmarkEligibilityPolicy, BenchmarkEligibilityPolicy>();
        builder.Services.AddSingleton<IBenchmarkRuntimeSnapshotFactory, BenchmarkRuntimeSnapshotFactory>();

        // Singleton: it owns a file-hash cache with file-system watchers, so one instance per process, not per run.
        builder.Services.AddSingleton<IRuntimeEnvironmentFactsProvider, RuntimeEnvironmentFactsProvider>();
        builder.Services.AddOptions<BenchmarkEventBufferOptions>();
        builder.Services.AddOptions<BenchmarkQueueOptions>()
               .BindConfiguration(BenchmarkQueueOptions.SectionName)
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<BenchmarkQueueOptions>, BenchmarkQueueOptionsValidator>();
        builder.Services.AddSingleton<IBenchmarkEventBuffer, BenchmarkEventBuffer>();
        builder.Services.AddSingleton<IBenchmarkCancellationRegistry, BenchmarkCancellationRegistry>();
        builder.Services.AddScoped<IBenchmarkCancellationService, BenchmarkCancellationService>();
        builder.Services.AddSingleton<IBenchmarkQueueSignal, BenchmarkQueueSignal>();
        builder.Services.AddSingleton(BenchmarkAdmissionRetry.Default);
        builder.Services.AddScoped<IBenchmarkRunExecutor, BenchmarkRunExecutor>();
        builder.Services.AddScoped<IBenchmarkPythonTestsVerifier, BenchmarkPythonTestsVerifier>();
        builder.Services.AddScoped<IBenchmarkJudgeExecutor, BenchmarkJudgeExecutor>();
        builder.Services.AddScoped<IBenchmarkFidelityExecutor, BenchmarkFidelityExecutor>();
        builder.Services.AddScoped<IBenchmarkComparisonExecutor, BenchmarkComparisonExecutor>();
        builder.Services.AddScoped<IBenchmarkPairwiseFitter, BenchmarkPairwiseFitter>();
        builder.Services.AddScoped<IBenchmarkPairwisePlanner, BenchmarkPairwisePlanner>();
        builder.Services.AddSingleton<IBenchmarkPerplexityRunner, BenchmarkPerplexityRunner>();
        builder.Services.AddOptions<BenchmarkKldCacheOptions>().BindConfiguration(BenchmarkKldCacheOptions.SectionName);
        builder.Services.AddSingleton(static services => new BenchmarkKldBaseCache(services.GetRequiredService<IFreeSpaceProbe>()));
        builder.Services.AddHostedService<BenchmarkQueueHostedService>();
        return builder;
    }
}
