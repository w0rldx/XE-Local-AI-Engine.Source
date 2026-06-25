namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Eval;
using XE_Local_AI_Engine.Client.Services.Eval.Implementation;

internal static class AddNodeEvalExtensions
{
    public static IHostApplicationBuilder AddNodeEval(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // Golden conversation store. Free-text input turns, assertions, and rubrics are encrypted at rest; the eval
        // runner reads enabled rows and the CRUD service owns manual authoring.
        builder.Services.AddScoped<IGoldenConversationStore, GoldenConversationStore>();
        // Golden conversation CRUD service: validates manual authoring and ownership-guards deletes.
        builder.Services.AddScoped<IGoldenConversationService, GoldenConversationService>();
        // Eval model options. Defaults to the node-local chat model so golden text and agent output stay on-node.
        builder.Services.AddOptions<PlaybookEvalOptions>()
               .Bind(builder.Configuration.GetSection(PlaybookEvalOptions.Section))
               .PostConfigure(evalOptions =>
               {
                   if (string.IsNullOrWhiteSpace(evalOptions.ModelName))
                   {
                       evalOptions.ModelName = builder.Configuration.GetValue<string>("Ollama:ChatModel")
                                               ?? builder.Configuration.GetValue<string>("Agent:LocalChat:DefaultModel")
                                               ?? string.Empty;
                   }
               });
        // Eval judge: deterministic assertion path plus node-local judge path. Singleton because it holds no scoped
        // state and receives the per-run node-local client as a parameter.
        builder.Services.AddSingleton<IPlaybookEvalJudge, OllamaPlaybookEvalJudge>();
        // Eval orchestration: re-runs the real agent loop over the golden set, scores candidate-vs-baseline output, and
        // persists the plaintext EvalResult consumed by the promotion gate.
        builder.Services.AddScoped<IPlaybookEvalService, PlaybookEvalService>();

        return builder;
    }
}
