namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

internal static class AddNodeBenchmarksExtensions
{
    public static IHostApplicationBuilder AddNodeBenchmarks(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddScoped<IBenchmarkStore, BenchmarkStore>();
        builder.Services.AddScoped<IBenchmarkInstalledModelLeaseProvider, BenchmarkInstalledModelLeaseProvider>();
        builder.Services.AddScoped<IBenchmarkProjectService, BenchmarkProjectService>();
        builder.Services.AddScoped<IBenchmarkFreezeDependencyService, BenchmarkFreezeDependencyService>();
        builder.Services.AddScoped<IBenchmarkRunFreezeService, BenchmarkRunFreezeService>();
        builder.Services.AddSingleton<IBenchmarkEligibilityPolicy, BenchmarkEligibilityPolicy>();
        builder.Services.AddSingleton<IBenchmarkRuntimeSnapshotFactory, BenchmarkRuntimeSnapshotFactory>();
        builder.Services.AddOptions<BenchmarkEventBufferOptions>();
        builder.Services.AddOptions<BenchmarkQueueOptions>();
        builder.Services.AddSingleton<IBenchmarkEventBuffer, BenchmarkEventBuffer>();
        builder.Services.AddSingleton<IBenchmarkCancellationRegistry, BenchmarkCancellationRegistry>();
        builder.Services.AddScoped<IBenchmarkCancellationService, BenchmarkCancellationService>();
        builder.Services.AddSingleton<IBenchmarkQueueSignal, BenchmarkQueueSignal>();
        builder.Services.AddScoped<IBenchmarkRunExecutor, BenchmarkRunExecutor>();
        builder.Services.AddScoped<IBenchmarkJudgeExecutor, BenchmarkJudgeExecutor>();
        builder.Services.AddHostedService<BenchmarkQueueHostedService>();
        return builder;
    }
}
