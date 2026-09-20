namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using System.Threading.Channels;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;
using XE_Local_AI_Engine.Client.Services.Integrations.Tools.Implementation;

internal static class AddNodeIntegrationsExtensions
{
    /// <summary>
    ///     Registers the external-integration substrate: the options class, the four persistence stores and the
    ///     application services layered over them.
    /// </summary>
    /// <remarks>
    ///     No <c>IValidateOptions&lt;IntegrationOptions&gt;</c> is registered, and deliberately so: the class has no
    ///     cross-section invariant, so it validates its one <c>TimeSpan</c> bound through
    ///     <see cref="System.ComponentModel.DataAnnotations.IValidatableObject" /> while the annotations carry the rest.
    /// </remarks>
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

        // ONE ring per node and the only minter of an event sequence — a second instance would hand two readers of the same
        // execution different numbering. Singleton, disposed with the container because it owns a PeriodicTimer.
        builder.Services.AddSingleton<IIntegrationExecutionEventBuffer, IntegrationExecutionEventBuffer>();

        // The queue between the scoped accept path and the single-consumer coordinator. FullMode.Wait, never DropWrite: under
        // DropWrite a full channel returns TRUE and discards the id, stranding an admitted row nothing drains while it holds a slot.
        builder.Services.AddSingleton(static serviceProvider =>
            Channel.CreateBounded<Guid>(new BoundedChannelOptions(serviceProvider.GetRequiredService<IOptions<IntegrationOptions>>().Value.MaxQueuedExecutions)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            }));

        builder.Services.AddSingleton<IntegrationCancellationRegistry>();

        // One mutual-exclusion gate per caller-managed session id, shared by the scoped accept path and the scoped
        // session service — so a singleton, the same shape the cancellation registry has.
        builder.Services.AddSingleton<IntegrationSessionGate>();
        builder.Services.AddScoped(static serviceProvider => new IntegrationSessionService(serviceProvider.GetRequiredService<IIntegrationSessionStore>(),
            serviceProvider.GetRequiredService<IIntegrationExecutionStore>(),
            serviceProvider.GetRequiredService<IIntegrationTriggerStore>(),
            serviceProvider.GetRequiredService<IntegrationExternalAccess>(),
            serviceProvider.GetRequiredService<INodeChatPersistenceService>(),
            serviceProvider.GetRequiredService<IntegrationSessionGate>(),
            serviceProvider.GetRequiredService<TimeProvider>(),
            serviceProvider.GetRequiredService<ILogger<IntegrationSessionService>>()));
        builder.Services.AddScoped<IIntegrationInvocationService, IntegrationInvocationService>();

        // Two concrete classes with no interface, registered and injected as themselves: a one-implementation interface no ruling
        // asked for is scaffolding. Both take an INTERNAL collaborator, so they are constructed here, not by the container's activator.
        builder.Services.AddScoped(static serviceProvider => new IntegrationExecutionQueryService(serviceProvider.GetRequiredService<IIntegrationExecutionStore>(),
            serviceProvider.GetRequiredService<IIntegrationTriggerStore>(),
            serviceProvider.GetRequiredService<IIntegrationExecutionEventBuffer>(),
            serviceProvider.GetRequiredService<IntegrationCancellationRegistry>(),
            serviceProvider.GetRequiredService<TimeProvider>(),
            serviceProvider.GetRequiredService<ILogger<IntegrationExecutionQueryService>>()));
        builder.Services.AddScoped<IntegrationExternalAccess>();

        // The emit_output handler. Registering it surfaces the tool in the RESOLUTION seam only; the OFFER decides whether a run may
        // call it, holding it out of every projection for the coordinator to union in. Singleton, opening its own scope per call.
        builder.Services.AddSingleton<IClientLocalToolHandler, EmitOutputToolHandler>();

        // The single consumer of that channel. Hosted, so its startup sweep runs before the loop reads an id, and so
        // the loop stops with the host.
        builder.Services.AddHostedService<IntegrationExecutionCoordinator>();

        return builder;
    }
}
