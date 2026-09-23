namespace XE_Local_AI_Engine.Client;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Registers anonymous public-release self-update.
/// </summary>
/// <remarks>
///     The desktop-only endpoints are excluded from FastEndpoints ROUTING off the desktop flag via the
///     <c>IDesktopOnlyEndpoint</c> filter in <c>UseFastEndpoints</c>, but their backing services are registered in
///     every mode, because FastEndpoints instantiates every discovered endpoint at startup to evaluate that filter.
///     Off the desktop flag they are inert by construction — <see cref="AppUpdateHostContext.IsLocalMode" /> is false,
///     so <see cref="IAppUpdateService" /> makes no GitHub call and the startup check is not scheduled.
/// </remarks>
internal static class AddAppUpdateExtensions
{
    internal static IHostApplicationBuilder AddAppUpdate(this IHostApplicationBuilder builder,
        IConfiguration configuration,
        LaunchMode launchMode,
        IReadOnlyList<string> restartArgs,
        bool shellOwned = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(restartArgs);
        var isLocalMode = launchMode.IsLocalMode();

        // The artifact flavor config (public repo URL + stable/RC track), baked at publish into appsettings.AppUpdate.json.
        builder.Services.AddOptions<AppUpdateChannelOptions>()
               .Bind(configuration.GetSection(AppUpdateChannelOptions.SectionName));

        // Host facts the services can't derive: desktop mode + the args to re-pass on relaunch. The real desktop flag is
        // recorded here so the services no-op off the flag even though they are registered in every mode.
        builder.Services.AddSingleton(new AppUpdateHostContext
        {
            IsLocalMode = isLocalMode,
            IsShellOwned = shellOwned,
            DataDirectory = configuration[DesktopBootstrap.NodeDataDirectoryKey],
            RestartArgs = [.. restartArgs]
        });

        // Velopack update manager seam + the shared snapshot state + the orchestration service.
        builder.Services.AddSingleton<IVelopackUpdateManagerFactory, VelopackUpdateManagerFactory>();
        builder.Services.AddSingleton<IAppUpdateState, AppUpdateState>();
        builder.Services.AddSingleton<IAppUpdateService>(services => new AppUpdateService(
            services.GetRequiredService<IVelopackUpdateManagerFactory>(),
            services.GetRequiredService<IAppUpdateState>(),
            services.GetRequiredService<IOptions<AppUpdateChannelOptions>>(),
            services.GetRequiredService<AppUpdateHostContext>(),
            services.GetRequiredService<ILogger<AppUpdateService>>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<INodeSettingsStore>(),
            AppUpdateService.RetainLeaseUntilProcessExit));
        builder.Services.AddSingleton<AppUpdateShutdownCoordinator>();

        // The one-shot startup update check is the only desktop-gated registration: off the flag no check is ever
        // scheduled, so a headless / Aspire / CI run does no update work at all.
        if (isLocalMode)
        {
            builder.Services.AddHostedService<AppUpdateCheckService>();
        }

        return builder;
    }
}
