namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
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

        if (!configuration.GetValue($"{DevelopmentOptions.Section}:Enabled", defaultValue: true))
        {
            return builder;
        }

        builder.Services.AddScoped<IDevelopmentStore, DevelopmentStore>();
        builder.Services.AddScoped<IDevelopmentHostApplyPort, TrustedDevelopmentHostApplyPort>();
        builder.Services.AddScoped<IDevelopmentCoordinator, DevelopmentCoordinator>();
        builder.Services.AddSingleton<IDevelopmentArtifactBlobStore, ManagedDevelopmentArtifactBlobStore>();
        builder.Services.AddScoped<IDevelopmentWorkspaceProvider, DevelopmentWorkspaceProvider>();
        builder.Services.AddScoped<IDevelopmentPatchEvidenceService, DevelopmentPatchEvidenceService>();
        builder.Services.AddScoped<IDevelopmentEvidenceService, DevelopmentEvidenceService>();
        builder.Services.AddScoped<IDevelopmentCoderModel, DevelopmentCoderModel>();
        builder.Services.AddScoped<IDevelopmentCoderAttemptRunner, DevelopmentCoderAttemptRunner>();
        builder.Services.AddScoped<IDevelopmentValidationRunner, DevelopmentValidationRunner>();
        builder.Services.AddScoped<IDevelopmentReviewerModel, DevelopmentReviewerModel>();
        builder.Services.AddScoped<IDevelopmentReviewerAttemptRunner, DevelopmentReviewerAttemptRunner>();
        builder.Services.AddScoped<IDevelopmentApplyService, DevelopmentApplyService>();
        builder.Services.AddScoped<IDevelopmentRepositoryBindingService, DevelopmentRepositoryBindingService>();
        builder.Services.AddSingleton<DevelopmentCloudContextCatalog>();
        builder.Services.AddSingleton<IDevelopmentCloudContextCatalog>(static services => services.GetRequiredService<DevelopmentCloudContextCatalog>());
        builder.Services.AddSingleton<IDevelopmentCloudContextBuilder, DevelopmentCloudContextBuilder>();
        builder.Services.AddSingleton<DevelopmentCloudRoleRouteFactory>();
        builder.Services.AddScoped<IDevelopmentCloudAttemptContextService, DevelopmentCloudAttemptContextService>();
        builder.Services.AddSingleton<IDevelopmentCloudEgressAuditSink, LoggingDevelopmentCloudEgressAuditSink>();
        builder.Services.Replace(ServiceDescriptor.Singleton<ICloudEgressAuthorizer, DevelopmentCloudEgressAuthorizer>());
        builder.Services.AddSingleton<IDevelopmentAttemptLiveBroker, DevelopmentAttemptLiveBroker>();
        builder.Services.TryAddSingleton<IDevelopmentAttemptLiveEventPublisher, NullDevelopmentAttemptLiveEventPublisher>();
        builder.Services.AddSingleton<DevelopmentAttemptExecutionSupervisor>();
        builder.Services.AddSingleton<IDevelopmentAttemptExecutionSupervisor>(static services => services.GetRequiredService<DevelopmentAttemptExecutionSupervisor>());
        builder.Services.AddSingleton<IHostedService>(static services => services.GetRequiredService<DevelopmentAttemptExecutionSupervisor>());
        builder.Services.AddScoped<IDevelopmentManagementService, DevelopmentManagementService>();
        builder.Services.AddHostedService<DevelopmentStartupReconciler>();
        return builder;
    }
}
