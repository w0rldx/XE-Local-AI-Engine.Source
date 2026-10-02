namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Services.Analysis;
using XE_Local_AI_Engine.Client.Services.Analysis.Implementation;

internal static class AddNodeAnalysisExtensions
{
    public static IHostApplicationBuilder AddNodeAnalysis(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // Analysis options. The model name is the PlaybookAnalysisModelName node setting, read per run; blank inherits the default
        // model, so feedback comments are never sent to the cloud chat client by fallback.
        builder.Services.AddOptions<PlaybookAnalysisOptions>()
               .Bind(builder.Configuration.GetSection(PlaybookAnalysisOptions.Section));
        // Analysis agent: proposes suggested actions from feedback aggregates using a node-local model only. Singleton
        // because it holds no scoped state and receives a fresh per-run chat client.
        builder.Services.AddSingleton<IPlaybookAnalysisAgent, DefaultPlaybookAnalysisAgent>();
        // Analysis orchestration: gates on the occurrence threshold, validates proposal evidence, dedupes, and writes
        // suggested actions for human review.
        builder.Services.AddScoped<IPlaybookAnalysisService, PlaybookAnalysisService>();

        return builder;
    }
}
