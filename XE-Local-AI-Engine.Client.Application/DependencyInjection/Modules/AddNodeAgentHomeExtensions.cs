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
    /// <summary>
    ///     Registers the AgentHome gateway and its tools, plus the sandbox provider roles node agent work executes under.
    /// </summary>
    /// <remarks>
    ///     Sandbox providers register as themselves; the agent and Development ROLES are lazy factories over them. Two
    ///     roles naming one provider therefore share ONE singleton: a second <c>ProcessSandboxRuntimeProvider</c> takes a
    ///     second jail root, and <c>CoderWorkspaceReader.ConnectAsync</c>, which attaches to the live sandbox by key, would
    ///     find no workspace. No bare <c>ISandboxRuntimeProvider</c> is registered, so every consumer names a role; the
    ///     lazy factories also let <c>DockerSandboxRuntimeProvider</c> arrive later from <c>AddNodeContainerSandbox</c>.
    /// </remarks>
    public static IHostApplicationBuilder AddNodeAgentHome(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // ClientLocal run_in_agent_home tool: the handler flag-gates and validates requests before delegating through the
        // AgentHome gateway to the manifest initializer, sandbox provider and selected-folder resolver, and stays off the wire until AgentHome is enabled.
        builder.Services.AddSingleton<IAgentHomeIdentityProvider, AgentHomeIdentityProvider>();
        builder.Services.AddSingleton<IAgentHomeExecutionLeaseManager, AgentHomeExecutionLeaseManager>();
        builder.Services.AddSingleton<IAgentHomeWorkspaceIsolation, AgentHomeWorkspaceIsolation>();
        builder.Services.AddSingleton<IWorkspaceRevocationPreparation, AgentHomeWorkspaceRevocationPreparation>();
        // Workspace copy service: selected-folder copy with exclusions, symlink-escape guard, byte budget, and git baseline.
        builder.Services.AddSingleton<IAgentHomeWorkspaceService, AgentHomeWorkspaceService>();
        // Patch export service: post-run diff of the workspace-copy baseline with changes.patch, changed-files.json, and budget guard.
        builder.Services.AddSingleton<IAgentHomePatchService, AgentHomePatchService>();
        // Goal executor: the bounded inner agent loop that turns the model's `goal` into real workspace work. It takes the
        // shared IChatClient, so it stays out of AgentHomeService, which has no model dependency of its own.
        builder.Services.AddSingleton<IAgentHomeGoalExecutor, AgentHomeGoalExecutor>();
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
        // Sandbox provider selection, configuration-bound and resolved once: the deterministic fake or the jailed process provider.
        // No execution-capable default — unset is fake off Production; in Production SandboxOptionsValidator fails startup, so a stripped config never grants execution.
        builder.Services.AddOptions<SandboxOptions>()
               .Bind(configuration.GetSection(SandboxOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<SandboxOptions>, SandboxOptionsValidator>();
        // Development Mode's own provider selection, bound HERE and not in AddNodeDevelopment: that module returns early when
        // Development Mode is off, while the selector below reads this option unconditionally. Unset means "whatever the agent role resolved".
        builder.Services.AddOptions<DevelopmentSandboxOptions>()
               .Bind(configuration.GetSection(DevelopmentSandboxOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<DevelopmentSandboxOptions>, DevelopmentSandboxOptionsValidator>();
        // Local-container provider options — the copy-in and jail-growth byte budgets ProcessSandboxRuntimeProvider enforces.
        // Bound and validated unconditionally; the fail-closed validator only bites when the local-container provider is selected.
        builder.Services.AddOptions<LocalContainerOptions>()
               .Bind(configuration.GetSection(LocalContainerOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<LocalContainerOptions>, LocalContainerOptionsValidator>();
        // The containment probe measures ONCE per host which mechanisms a sandboxed child launches under: systemd user scope
        // (CPU/memory/PID), empty netns (egress), costly bubblewrap mounts (filesystem). Singleton: launcher and advertised Capabilities share it.
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

        // The concrete providers register as themselves and the two ROLES are lazy factories over them; see this method's
        // remarks for why one provider named by both roles must stay a single instance.
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
        // Read-only projection of the on-disk run history; no database row exists for a run.
        builder.Services.AddSingleton<IAgentHomeRunListService, AgentHomeRunListService>();
        // Run-directory retention; nothing else ever deletes a run. Registered LAST on purpose: the sweep's first act
        // is a filesystem walk, which must not start ahead of the orphan reaper's process gates.
        builder.Services.AddOptions<AgentHomeRunRetentionOptions>()
               .Bind(configuration.GetSection(AgentHomeRunRetentionOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<AgentHomeRunRetentionOptions>, AgentHomeRunRetentionOptionsValidator>();
        builder.Services.AddHostedService<AgentHomeRunRetentionService>();

        return builder;
    }
}
