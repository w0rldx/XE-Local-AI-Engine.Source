namespace XE_Local_AI_Engine.Providers.Capabilities;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Capabilities.Contracts;
using XE_Local_AI_Engine.Providers.Capabilities.Implementation;
using XE_Local_AI_Engine.Providers.Capabilities.Options;

/// <summary>
///     DI wiring for the cross-platform hardware profiler. Mirrors the other provider projects' self-contained
///     <c>Add…</c> extension so the host composition root only references this project, never the internal probe seams.
/// </summary>
public static class CapabilitiesServiceCollectionExtensions
{
    /// <summary>
    ///     Registers <see cref="IHardwareProfiler" /> → <see cref="HardwareProfiler" /> as a singleton (the profile is
    ///     cached in-memory and re-probed only on <c>forceRefresh:true</c>), along with the live process/environment
    ///     probe seams. <paramref name="modelsVolumePath" /> is the models/content-root path whose volume the free-disk
    ///     figure is reported for; when <see langword="null" /> the process working directory is used.
    /// </summary>
    public static IServiceCollection AddHardwareProfiler(this IServiceCollection services,
        string? modelsVolumePath = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = string.IsNullOrWhiteSpace(modelsVolumePath)
            ? new HardwareProfilerOptions()
            : new HardwareProfilerOptions
            {
                ModelsVolumePath = modelsVolumePath
            };

        services.AddSingleton(options);
        services.AddSingleton<IProcessProbe, ProcessProbe>();
        services.AddSingleton<IHardwareProbeEnvironment, HardwareProbeEnvironment>();
        services.AddSingleton<IHardwareProfiler, HardwareProfiler>();

        // The probe-timeout metrics seam degrades to a no-op unless the host wires a NodeMetrics-backed implementation
        // (the profiler layer cannot reference the application meter). TryAdd so a host registration always wins.
        services.TryAddSingleton<IHardwareProbeMetrics, NullHardwareProbeMetrics>();

        return services;
    }
}
