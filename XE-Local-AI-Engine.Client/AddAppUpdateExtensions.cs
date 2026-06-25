namespace XE_Local_AI_Engine.Client;

using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>
///     Registers the app self-update + GitHub device-flow stack. The desktop-only endpoints are excluded from FastEndpoints
///     ROUTING off the desktop flag via the <c>IDesktopOnlyEndpoint</c> filter in <c>UseFastEndpoints</c>, but their
///     backing services are registered in every mode: FastEndpoints instantiates every discovered endpoint at startup to
///     evaluate the routing filter, so the services must resolve even when the routes will be filtered out. Off the desktop
///     flag the services are inert by construction — <see cref="AppUpdateHostContext.IsDesktop" /> is false, so
///     <see cref="IAppUpdateService" /> makes no GitHub call — and the startup check is not scheduled, so a
///     headless / Aspire / CI run does no update work.
/// </summary>
internal static class AddAppUpdateExtensions
{
    internal static IHostApplicationBuilder AddAppUpdate(this IHostApplicationBuilder builder,
        IConfiguration configuration,
        bool isDesktop,
        string[] processArgs)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(processArgs);

        // The build-flavor channel config (repo URL + GitHub App client_id), baked at publish into
        // appsettings.AppUpdate.json and layered over the appsettings defaults in Program.cs. Public config, validated
        // lightly — an unbaked build (empty values) leaves the updater inert via AppUpdateChannelOptions.IsConfigured.
        builder.Services.AddOptions<AppUpdateChannelOptions>()
               .Bind(configuration.GetSection(AppUpdateChannelOptions.SectionName));

        // Host facts the services can't derive: desktop mode + the args to re-pass on relaunch. The real desktop flag is
        // recorded here so the services no-op off the flag even though they are registered in every mode.
        builder.Services.AddSingleton(new AppUpdateHostContext(isDesktop, RestartArgs: processArgs));

        // Encrypted GitHub session store (IDataProtector, github-token.enc) — fourth instance of the .enc token pattern.
        builder.Services.AddSingleton<IGitHubTokenStore, GitHubTokenStore>();

        // Server-side holder for the in-flight device_code so poll never receives it from React.
        builder.Services.AddSingleton<IGitHubDeviceFlowSession, GitHubDeviceFlowSession>();

        // GitHub device-flow auth service with its own named HttpClient (HTTPS-only github.com). A named client resolved
        // via IHttpClientFactory mirrors the Codex auth registration. Auto-redirect is disabled: every request targets a
        // fixed github.com/api.github.com URL and no redirect is expected, so refusing to follow one prevents the Bearer
        // header (or device-flow body) from being replayed to an unexpected host on any future cross-host redirect.
        builder.Services.AddHttpClient(GitHubAuthService.HttpClientName)
               .ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler
               {
                   AllowAutoRedirect = false
               });
        builder.Services.AddSingleton<IGitHubAuthService, GitHubAuthService>();

        // Velopack update manager seam (per-token factory) + the shared snapshot state + the orchestration service.
        builder.Services.AddSingleton<IVelopackUpdateManagerFactory, VelopackUpdateManagerFactory>();
        builder.Services.AddSingleton<IAppUpdateState, AppUpdateState>();
        builder.Services.AddSingleton<IAppUpdateService, AppUpdateService>();

        // The one-shot startup update check is the only desktop-gated registration: off the flag no check is ever
        // scheduled, so a headless / Aspire / CI run does no update work at all.
        if (isDesktop)
        {
            builder.Services.AddHostedService<AppUpdateCheckService>();
        }

        return builder;
    }
}
