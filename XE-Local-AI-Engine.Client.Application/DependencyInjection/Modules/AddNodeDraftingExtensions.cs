namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.Options;
using OllamaSharp;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Drafting;
using XE_Local_AI_Engine.Client.Services.Drafting.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

internal static class AddNodeDraftingExtensions
{
    public static IHostApplicationBuilder AddNodeDrafting(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.Services.AddOptions<DraftingOptions>()
               .Bind(builder.Configuration.GetSection(DraftingOptions.Section))
               .PostConfigure(static draftingOptions =>
               {
                   // Every value is a ceiling that bounds how long one request can hold the single draft slot, so a
                   // non-positive value would mean "unbounded" rather than "disabled" — reset each to its default.
                   if (draftingOptions.MaxPromptChars < 1)
                   {
                       draftingOptions.MaxPromptChars = 60000;
                   }

                   if (draftingOptions.MaxOutputTokens < 1)
                   {
                       draftingOptions.MaxOutputTokens = 8192;
                   }

                   // A non-positive explicit override would mean "unbounded"; drop it back to null so the generation
                   // follows the node's live "Maximum message request timeout" instead of a second hardcoded ceiling.
                   if (draftingOptions.GenerationTimeout <= TimeSpan.Zero)
                   {
                       draftingOptions.GenerationTimeout = null;
                   }
               });

        // Singleton: the draft slot is process-wide, and it reads the two singleton busy signals.
        builder.Services.AddSingleton<DraftAdmissionGate>();

        // Scoped, not singleton: eligibility reads the scoped, DbContext-backed IModelClassificationStore. The Ollama
        // API client is resolved OPTIONALLY — the Ollama runtime is capability-gated, and its absence must make Ollama
        // models ineligible rather than break the drafting surface on a llama.cpp-only node.
        builder.Services.AddScoped<IConfigDraftService>(serviceProvider => new DefaultConfigDraftService(serviceProvider.GetRequiredService<ILocalModelProviderResolver>(),
            serviceProvider.GetRequiredService<IGgufModelStore>(),
            serviceProvider.GetRequiredService<IModelClassificationStore>(),
            serviceProvider.GetRequiredService<DraftAdmissionGate>(),
            serviceProvider.GetRequiredService<IOptions<DraftingOptions>>(),
            serviceProvider.GetRequiredService<INodeSettingsStore>(),
            serviceProvider.GetRequiredService<TimeProvider>(),
            serviceProvider.GetRequiredService<ILogger<DefaultConfigDraftService>>(),
            serviceProvider.GetService<IOllamaApiClient>()));

        return builder;
    }
}
