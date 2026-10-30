namespace XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

using Microsoft.Extensions.DependencyInjection;
using Quartz;

/// <summary>
///     Shuts down every scheduler a test provider started when the provider is disposed, so a test that fails before its
///     own <c>Shutdown</c> cannot leave one running.
/// </summary>
/// <remarks>
///     A started Quartz 4 ADO store runs its misfire handler on a foreground thread, and disposing the container does not
///     stop the scheduler, so one leaked scheduler keeps the whole test host alive after its last test. Register it with
///     <see cref="AddTo" />: it is resolved right after the factory, so the provider disposes it first.
/// </remarks>
internal sealed class SchedulerShutdownGuard : IAsyncDisposable
{
    private readonly ISchedulerFactory _schedulerFactory;

    public SchedulerShutdownGuard(ISchedulerFactory schedulerFactory)
    {
        _schedulerFactory = schedulerFactory;
    }

    public static ServiceProvider AddTo(IServiceCollection services)
    {
        services.AddSingleton<SchedulerShutdownGuard>();
        var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<SchedulerShutdownGuard>();
        return provider;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var scheduler in await _schedulerFactory.GetAllSchedulers(CancellationToken.None))
        {
            if (scheduler.Status is not SchedulerStatus.Shutdown)
            {
                await scheduler.Shutdown(waitForJobsToComplete: false, CancellationToken.None);
            }
        }
    }
}
