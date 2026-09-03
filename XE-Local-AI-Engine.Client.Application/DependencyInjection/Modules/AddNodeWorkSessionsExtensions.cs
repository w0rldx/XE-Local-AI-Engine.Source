namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Tools.Implementation;

internal static class AddNodeWorkSessionsExtensions
{
    /// <summary>
    ///     Registers the work-session persistence substrate.
    ///     <para>
    ///         Unlike the Development module, <c>Enabled=false</c> does <em>not</em> skip registration: the REST surface
    ///         and the hub are mapped unconditionally, so an empty container would answer 500 where a disabled node has
    ///         to answer legibly. The switch is enforced in the reconciler and, later, in the runtime.
    ///     </para>
    /// </summary>
    public static IHostApplicationBuilder AddNodeWorkSessions(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.Services.AddOptions<WorkSessionOptions>()
               .Bind(configuration.GetSection(WorkSessionOptions.Section))
               .ValidateDataAnnotations()
               .ValidateOnStart();

        // Cross-section: the park clock has to expire before the node's pending tool-call clock does, and neither
        // section's data annotations can see the other.
        builder.Services.AddSingleton<IValidateOptions<WorkSessionOptions>, WorkSessionOptionsValidator>();

        builder.Services.AddScoped<IAgentWorkSessionStore, AgentWorkSessionStore>();
        builder.Services.AddSingleton<IWorkSessionArtifactBlobStore, ManagedWorkSessionArtifactBlobStore>();
        builder.Services.AddHostedService<WorkSessionStartupReconciler>();

        // The same instance behind both surfaces: one class decides who may move a session's status, and a second
        // instance would let the two disagree about what it already did.
        //
        // Both roles resolve the CONCRETE registration rather than casting the interface. The cast read equivalently
        // and was not: replacing IWorkSessionService — with a test double, or one day a decorator — made this second
        // surface throw an InvalidCastException while the host was still being built, from a registration that had
        // nothing to do with the replacement.
        builder.Services.AddScoped<WorkSessionService>();
        builder.Services.AddScoped<IWorkSessionService>(services => services.GetRequiredService<WorkSessionService>());
        builder.Services.AddScoped<IWorkflowOwnedWorkSessionLifecycle>(services => services.GetRequiredService<WorkSessionService>());
        builder.Services.AddScoped<WorkSessionCheckpointComposer>();

        // Scoped: it reads the conversation through the scoped chat persistence, from the supervisor's per-turn scope.
        builder.Services.AddScoped<WorkSessionStepContextBound>();

        // Scoped for the same reason: it reads the agent definition through the scoped store. Resolved by the REST
        // service on create/repoint AND by the supervisor's per-turn scope, so both judge a session's tool gates the
        // same way.
        builder.Services.AddScoped<WorkSessionToolGate>();

        // Scoped for the same reason, and resolved from the same two places: the development-workflow lane before it
        // creates a session, and the supervisor's per-turn scope before every step it sends.
        builder.Services.AddScoped<WorkSessionWriteDeclarationGuard>();

        // One instance serving three roles: the supervisor holds the in-flight runs, so a second instance would answer
        // "not running" to every stop and drive a session twice.
        builder.Services.AddSingleton<WorkSessionExecutionSupervisor>();
        builder.Services.AddSingleton<IWorkSessionExecutionSupervisor>(services => services.GetRequiredService<WorkSessionExecutionSupervisor>());
        builder.Services.AddSingleton<IHostedService>(services => services.GetRequiredService<WorkSessionExecutionSupervisor>());
        builder.Services.AddHostedService<WorkSessionAgentSeeder>();

        // Singletons because ClientLocalToolRegistry captures the IClientLocalToolHandler enumerable once at
        // construction; each handler opens its own scope per call for the scoped store.
        builder.Services.AddSingleton<IClientLocalToolHandler, UpdateWorkPlanToolHandler>();
        builder.Services.AddSingleton<IClientLocalToolHandler, RecordFindingToolHandler>();
        builder.Services.AddSingleton<IClientLocalToolHandler, SaveArtifactToolHandler>();
        builder.Services.AddSingleton<IClientLocalToolHandler, CompleteWorkSessionToolHandler>();

        // The hub's publisher wins wherever it is registered; this keeps a hub-less host resolvable, the same pairing
        // the permissive and node tool-approval policies use.
        builder.Services.TryAddSingleton<IWorkSessionEventPublisher, NoOpWorkSessionEventPublisher>();

        // Reserved: no work-session tool needs a jail in v1, so nothing injects this yet.
        builder.Services.AddSingleton(SandboxProviderSelector.ResolveWorkSession);
        return builder;
    }
}
