namespace XE_Local_AI_Engine.Providers.Training;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Providers.Training.Implementation;

/// <summary>
///     DI wiring for the uv/venv/subprocess mechanics of the Python training runtime (ADR 0005 §3). Only mechanics live
///     here — the durable queue, persistence, orchestration and endpoints belong to <c>Client.Application</c>.
/// </summary>
/// <remarks>
///     <strong>Caller contract:</strong> the host must also register an <see cref="ITrainingRuntimeEventPublisher" />.
///     A no-op default is registered here so provider-only and test hosts resolve, and the Client host replaces it with
///     the SignalR-backed publisher — <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService}" />
///     means whichever the host registers first wins, so the host registers before calling this.
/// </remarks>
public static class TrainingServiceCollectionExtensions
{
    public static IServiceCollection AddTrainingRuntime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ITrainingRuntimeEventPublisher, NoOpTrainingRuntimeEventPublisher>();
        services.AddHttpClient(nameof(TrainingRuntimeService));

        services.TryAddSingleton<ITrainingRuntimePrerequisiteProbe>(static _ =>
            new TrainingRuntimePrerequisiteProbe(new LinuxTrainingProcessRunner(),
                TrainingRuntimeLayout.DefaultCacheRoot(),
                TrainingRuntimeLayout.ResolveScriptsDirectory()));

        // Spawn-and-return launcher plus the /proc reader its receipts are validated against. Both are stateless
        // singletons; the run executor and the startup reaper are the only consumers.
        services.TryAddSingleton<ITrainingProcessSpawner>(static _ => new LinuxTrainingProcessSpawner());
        services.TryAddSingleton<ITrainingProcessInspector>(static sp => new LinuxTrainingProcessInspector(sp.GetService<TimeProvider>()));

        services.TryAddSingleton<ITrainingRuntimeService>(static sp =>
            new TrainingRuntimeService(sp.GetRequiredService<ITrainingRuntimePrerequisiteProbe>(),
                sp.GetRequiredService<ITrainingRuntimeEventPublisher>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(TrainingRuntimeService)),
                sp.GetRequiredService<ILogger<TrainingRuntimeService>>()));

        return services;
    }

    /// <summary>Swallows status pushes for hosts with no transport (tests, provider-only composition).</summary>
    private sealed class NoOpTrainingRuntimeEventPublisher : ITrainingRuntimeEventPublisher
    {
        public Task PublishStatusAsync(TrainingRuntimeStatusHubEvent statusEvent, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
