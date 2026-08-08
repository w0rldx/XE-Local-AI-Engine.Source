namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.Coder;
using XE_Local_AI_Engine.Client.Services.Coder.Implementation;
using XE_Local_AI_Engine.Client.Services.Coder.Tools.Implementation;

internal static class AddNodeCoderExtensions
{
    public static IHostApplicationBuilder AddNodeCoder(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // Read-only coder tools (list_files / read_file / search_text). They share the AgentHome sandbox + identity +
        // secret-exclusion services (registered by AddNodeAgentHome / AddNodeWorkerInfrastructure) and are gated by the
        // same AgentHome:Enabled flag — there is no Coder:Enabled. The handlers, reader, and options are
        // all Singleton: ClientLocalToolRegistry captures the IClientLocalToolHandler IEnumerable at
        // construction, so a scoped handler would be a captive dependency. WorkspacePathGuard is static (no registration).
        builder.Services.AddOptions<CoderOptions>()
               .Bind(configuration.GetSection(CoderOptions.SectionName));

        builder.Services.AddSingleton<ICoderWorkspaceReader, CoderWorkspaceReader>();
        builder.Services.AddSingleton<IClientLocalToolHandler, ListFilesToolHandler>();
        builder.Services.AddSingleton<IClientLocalToolHandler, ReadFileToolHandler>();
        builder.Services.AddSingleton<IClientLocalToolHandler, SearchTextToolHandler>();

        return builder;
    }
}
