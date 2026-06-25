namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Eval;
using XE_Local_AI_Engine.Client.Services.Eval.Implementation;

internal static class AddNodeGoldenHarvestExtensions
{
    public static IHostApplicationBuilder AddNodeGoldenHarvest(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // Golden harvest read boundary: reconstructs harvest candidates from an agent's thumbs-up assistant turns
        // (plaintext thumbs-up scan via parameterized raw ADO, decrypted turn content via NodeChatDbContext). Scoped to
        // match the scoped, DbContext-backed store.
        builder.Services.AddScoped<IGoldenHarvestSourceStore, GoldenHarvestSourceStore>();
        // Golden harvest options: server-side caps on candidates persisted per run and most-recent thumbs-up sources
        // scanned. No model name — harvest is deterministic and invokes no LLM, so nothing is defaulted at composition.
        builder.Services.AddOptions<GoldenHarvestOptions>()
               .Bind(builder.Configuration.GetSection(GoldenHarvestOptions.Section));
        // Golden harvest orchestration: deterministically scans thumbs-up sources, dedups against already-harvested
        // messages, and stages each fresh candidate inert via the golden CRUD service (same validation/caps/encryption).
        // Scoped to match the scoped stores/service it composes.
        builder.Services.AddScoped<IGoldenHarvestService, GoldenHarvestService>();

        return builder;
    }
}
