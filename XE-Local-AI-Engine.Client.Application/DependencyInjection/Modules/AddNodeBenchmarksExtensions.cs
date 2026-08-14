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
        builder.Services.AddSingleton<IBenchmarkEligibilityPolicy, BenchmarkEligibilityPolicy>();
        builder.Services.AddSingleton<IBenchmarkRuntimeSnapshotFactory, BenchmarkRuntimeSnapshotFactory>();
        return builder;
    }
}
