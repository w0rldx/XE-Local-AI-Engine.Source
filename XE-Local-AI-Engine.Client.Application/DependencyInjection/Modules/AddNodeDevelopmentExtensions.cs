namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;

internal static class AddNodeDevelopmentExtensions
{
    public static IHostApplicationBuilder AddNodeDevelopment(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.Services.AddOptions<DevelopmentOptions>()
               .Bind(configuration.GetSection(DevelopmentOptions.Section))
               .ValidateDataAnnotations()
               .ValidateOnStart();

        if (!configuration.GetValue($"{DevelopmentOptions.Section}:Enabled", defaultValue: false))
        {
            return builder;
        }

        builder.Services.AddScoped<IDevelopmentStore, DevelopmentStore>();
        builder.Services.TryAddScoped<IDevelopmentHostApplyPort, UnavailableDevelopmentHostApplyPort>();
        builder.Services.AddScoped<IDevelopmentCoordinator, DevelopmentCoordinator>();
        builder.Services.AddSingleton<IDevelopmentArtifactBlobStore, ManagedDevelopmentArtifactBlobStore>();
        builder.Services.AddHostedService<DevelopmentStartupReconciler>();
        return builder;
    }
}

