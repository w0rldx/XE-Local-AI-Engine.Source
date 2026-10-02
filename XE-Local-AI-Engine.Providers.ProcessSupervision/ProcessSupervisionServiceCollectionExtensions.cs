namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Providers.Abstractions.Diagnostics;

/// <summary>DI registration shared by the runtime providers that launch child processes.</summary>
public static class ProcessSupervisionServiceCollectionExtensions
{
    /// <summary>Registers the one <see cref="ChildProcessOutputTailRegistry" />, also as <see cref="IChildProcessOutputTails" />; idempotent.</summary>
    public static IServiceCollection AddChildProcessOutputTails(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ChildProcessOutputTailRegistry>();
        services.TryAddSingleton<IChildProcessOutputTails>(static sp => sp.GetRequiredService<ChildProcessOutputTailRegistry>());
        return services;
    }
}
