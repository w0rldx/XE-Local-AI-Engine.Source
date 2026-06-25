namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Services.Analysis;
using XE_Local_AI_Engine.Client.Services.Analysis.Implementation;

internal static class AddNodeAnalysisExtensions
{
    public static IHostApplicationBuilder AddNodeAnalysis(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // Analysis model options. Defaults to the node-local chat model so feedback comments are never sent to the
        // cloud chat client by fallback.
        builder.Services.AddOptions<PlaybookAnalysisOptions>()
               .Bind(builder.Configuration.GetSection(PlaybookAnalysisOptions.Section))
               .PostConfigure(analysisOptions =>
               {
                   if (string.IsNullOrWhiteSpace(analysisOptions.ModelName))
                   {
                       analysisOptions.ModelName = builder.Configuration.GetValue<string>("Ollama:ChatModel")
                                                   ?? builder.Configuration.GetValue<string>("Agent:LocalChat:DefaultModel")
                                                   ?? string.Empty;
                   }
               });
        // Analysis agent: proposes suggested actions from feedback aggregates using a node-local model only. Singleton
        // because it holds no scoped state and receives a fresh per-run chat client.
        builder.Services.AddSingleton<IPlaybookAnalysisAgent, OllamaPlaybookAnalysisAgent>();
        // Analysis orchestration: gates on the occurrence threshold, validates proposal evidence, dedupes, and writes
        // suggested actions for human review.
        builder.Services.AddScoped<IPlaybookAnalysisService, PlaybookAnalysisService>();

        return builder;
    }
}
