namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;

internal static class AddNodeIntegrationsExtensions
{
    /// <summary>
    ///     Registers the external-integration substrate: the options class, the four persistence stores and the
    ///     application services layered over them.
    ///     <para>
    ///         No <c>IValidateOptions&lt;IntegrationOptions&gt;</c> is registered, and deliberately so — the class has no
    ///         cross-section invariant, so it validates itself through <see cref="System.ComponentModel.DataAnnotations.IValidatableObject" />
    ///         for its one <c>TimeSpan</c> bound while the annotations carry the rest.
    ///     </para>
    /// </summary>
    public static IHostApplicationBuilder AddNodeIntegrations(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.Services.AddOptions<IntegrationOptions>()
               .Bind(configuration.GetSection(IntegrationOptions.Section))
               .ValidateDataAnnotations()
               .ValidateOnStart();

        builder.Services.AddScoped<IIntegrationTriggerStore, IntegrationTriggerStore>();
        builder.Services.AddScoped<IIntegrationApiKeyStore, IntegrationApiKeyStore>();
        builder.Services.AddScoped<IIntegrationSessionStore, IntegrationSessionStore>();
        builder.Services.AddScoped<IIntegrationExecutionStore, IntegrationExecutionStore>();

        builder.Services.AddScoped<IIntegrationTriggerService, IntegrationTriggerService>();
        builder.Services.AddScoped<IIntegrationApiKeyService, IntegrationApiKeyService>();

        return builder;
    }
}
