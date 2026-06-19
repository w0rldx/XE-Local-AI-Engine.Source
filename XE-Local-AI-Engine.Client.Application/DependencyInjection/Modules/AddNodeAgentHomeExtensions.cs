namespace XE_Local_AI_Engine.Client;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;

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
        builder.Services.AddSingleton<IAgentHomeToolGateway, AgentHomeToolGateway>();
        builder.Services.AddSingleton<IClientLocalToolHandler, RunInAgentHomeToolHandler>();
        // Sandbox provider selection. The provider is configuration-bound and resolved once; the default is the
        // deterministic fake, while local-container selects the HostAgent-backed provider.
        builder.Services.AddOptions<SandboxOptions>()
               .Bind(configuration.GetSection(SandboxOptions.SectionName));
        // Local-container provider options. Bound and validated unconditionally; the fail-closed validator matters only
        // when the local-container provider is selected. The provider is a thin gRPC client that reuses HostAgent options.
        builder.Services.AddOptions<LocalContainerOptions>()
               .Bind(configuration.GetSection(LocalContainerOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<LocalContainerOptions>, LocalContainerOptionsValidator>();
        builder.Services.AddSingleton(SandboxProviderSelector.Resolve);
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
