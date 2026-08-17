namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;
using XE_Local_AI_Engine.Client.Services.Workspace;

internal static class AddNodeAgentHomeExtensions
{
    public static IHostApplicationBuilder AddNodeAgentHome(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // ClientLocal run_in_agent_home tool. The handler flag-gates and validates requests before delegating through
        // the AgentHome gateway to the manifest initializer, sandbox provider, and selected-folder resolver. The tool
        // stays off the distributed wire until AgentHome is enabled.
        builder.Services.AddSingleton<IAgentHomeIdentityProvider, AgentHomeIdentityProvider>();
        builder.Services.AddSingleton<IAgentHomeExecutionLeaseManager, AgentHomeExecutionLeaseManager>();
        builder.Services.AddSingleton<IAgentHomeWorkspaceIsolation, AgentHomeWorkspaceIsolation>();
        builder.Services.AddSingleton<IWorkspaceRevocationPreparation, AgentHomeWorkspaceRevocationPreparation>();
        // Workspace copy service: selected-folder copy with exclusions, symlink-escape guard, byte budget, and git baseline.
        builder.Services.AddSingleton<IAgentHomeWorkspaceService, AgentHomeWorkspaceService>();
        // Patch export service: post-run diff of the workspace-copy baseline with changes.patch, changed-files.json, and budget guard.
        builder.Services.AddSingleton<IAgentHomePatchService, AgentHomePatchService>();
        // Memory-proposal export service: gated collection of agent-written JSONL proposals with schema validation and secret scan.
        builder.Services.AddSingleton<IAgentHomeMemoryProposalService, AgentHomeMemoryProposalService>();
        // Run-scoped JSONL logger. The AgentHome gateway constructs one per run; the logger owns redacted event output.
        builder.Services.AddTransient<IAgentHomeRunLogger, AgentHomeRunLogger>();
        // Host patch-apply service: approval-gated landing of exported changes.patch onto selected host folders.
        builder.Services.AddScoped<INodePatchApplyService, NodePatchApplyService>();
        builder.Services.AddSingleton<IAgentHomeService, AgentHomeService>();
        // The chat agent-mode attachment stager is the SAME AgentHomeService singleton, so its conversation re-stage
        // shares the owner-node execution lease with run_in_agent_home rather than racing it on the node sandbox.
        builder.Services.AddSingleton<IConversationSandboxStager>(static sp => (AgentHomeService)sp.GetRequiredService<IAgentHomeService>());
        builder.Services.AddSingleton<IAgentHomeToolGateway, AgentHomeToolGateway>();
        builder.Services.AddSingleton<IClientLocalToolHandler, RunInAgentHomeToolHandler>();
        // Sandbox provider selection. The provider is configuration-bound and resolved once; known providers are the
        // deterministic fake and the jailed process provider. There is no execution-capable code default — an unset
        // provider resolves to fake in non-Production, while SandboxOptionsValidator fails startup in Production (a
        // stripped config must never silently grant the command-executing provider).
        builder.Services.AddOptions<SandboxOptions>()
               .Bind(configuration.GetSection(SandboxOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<SandboxOptions>, SandboxOptionsValidator>();
        // Development Mode's own provider selection. Bound HERE rather than in AddNodeDevelopment because that
        // module returns early when Development Mode is disabled, while the selector registered below must be able to
        // read this option unconditionally. Unset means "whatever the agent role resolved", so binding it changes
        // nothing on a node that does not set it.
        builder.Services.AddOptions<DevelopmentSandboxOptions>()
               .Bind(configuration.GetSection(DevelopmentSandboxOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<DevelopmentSandboxOptions>, DevelopmentSandboxOptionsValidator>();
        // Local-container provider options (the copy-in and jail-growth byte budgets ProcessSandboxRuntimeProvider
        // enforces). Bound and validated unconditionally; the fail-closed validator matters only when the
        // local-container provider is selected.
        builder.Services.AddOptions<LocalContainerOptions>()
               .Bind(configuration.GetSection(LocalContainerOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<LocalContainerOptions>, LocalContainerOptionsValidator>();
        // Sandbox containment. The probe measures ONCE per host which mechanisms a sandboxed child can really be
        // launched under (systemd user scope for CPU/memory/PID ceilings, empty network namespace for egress denial),
        // and is a singleton precisely so that measurement is shared: the provider's advertised Capabilities and the
        // launch path both read it, which is what makes "advertise only what is enforced" mechanical rather than a
        // convention someone has to remember.
        builder.Services.AddSingleton<ISandboxContainmentProbe, HostSandboxContainmentProbe>();
        builder.Services.AddSingleton<ISandboxLauncher, SandboxLauncher>();
        builder.Services.AddSingleton<ISandboxMarkerStore, FileSandboxMarkerStore>();
        // Group signalling is a Linux mechanism (setsid + kill(-pgid)); elsewhere no marker is ever written, so the
        // no-op keeps the reaper's logic identical while its /proc and libc paths stay off platforms without them.
        if (OperatingSystem.IsLinux())
        {
            builder.Services.AddSingleton<ISandboxProcessGroupKiller, LinuxSandboxProcessGroupKiller>();
        }
        else
        {
            builder.Services.AddSingleton<ISandboxProcessGroupKiller, NoOpSandboxProcessGroupKiller>();
        }

        // The concrete providers are registered as themselves, and the two ROLES are factories over them. Two things
        // follow, both deliberate. (1) When the agent and Development roles name the same provider they get the SAME
        // SINGLETON: ProcessSandboxRuntimeProvider allocates its jail root once per instance, and CoderWorkspaceReader
        // reaches AgentHome's live sandbox by attach key through ConnectAsync — a second instance would answer "no
        // workspace available" to every coder tool. (2) There is no ISandboxRuntimeProvider registration at all, so a
        // new consumer must state which role it wants rather than silently inheriting whichever provider won.
        // DockerSandboxRuntimeProvider is registered in AddNodeContainerSandbox, which runs after this module; the
        // roles are lazy factory delegates, so that ordering is fine.
        builder.Services.AddSingleton<FakeSandboxRuntimeProvider>();
        builder.Services.AddSingleton<ProcessSandboxRuntimeProvider>();
        builder.Services.AddSingleton(SandboxProviderSelector.ResolveAgent);
        builder.Services.AddSingleton(SandboxProviderSelector.ResolveDevelopment);
        // Startup sweep for sandbox children orphaned by a hard host kill (which skips the provider's Dispose/KillAsync
        // paths entirely), mirroring AddHostedService<StaleLlamaServerReaper> in the llama.cpp provider.
        builder.Services.AddHostedService<SandboxOrphanReaper>();
        // AgentHome layout initializer. Materializes the worker-local /agent-home tree idempotently and can run while
        // AgentHome itself is disabled.
        builder.Services.AddOptions<AgentHomeOptions>()
               .Bind(configuration.GetSection(AgentHomeOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<AgentHomeOptions>, AgentHomeOptionsValidator>();
        builder.Services.AddSingleton<IAgentHomeManifestService, AgentHomeManifestService>();

        return builder;
    }
}
