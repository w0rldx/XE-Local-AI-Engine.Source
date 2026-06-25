namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed class AgentTemplateImportService(IAgentTemplateCatalog catalog, IAgentDefinitionStore store) : IAgentTemplateImportService
{
    private readonly IAgentTemplateCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly IAgentDefinitionStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<AgentTemplateImportResult> ImportAsync(IReadOnlyList<string> slugs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slugs);

        // Dedupe the request so the same slug supplied twice yields one decision, not two competing buckets.
        var requestedSlugs = slugs
                             .Where(slug => !string.IsNullOrWhiteSpace(slug))
                             .Distinct(StringComparer.Ordinal)
                             .ToArray();

        var alreadySeeded = await _store.ListSeededSlugsAsync(cancellationToken).ConfigureAwait(false);

        var imported = new List<string>();
        var skippedExisting = new List<string>();
        var unknown = new List<string>();

        foreach (var slug in requestedSlugs)
        {
            var template = _catalog.TryGet(slug);
            if (template is null)
            {
                unknown.Add(slug);
                continue;
            }

            if (alreadySeeded.Contains(slug))
            {
                skippedExisting.Add(slug);
                continue;
            }

            _ = await _store.AddSeededAsync(ToInput(template), slug, cancellationToken).ConfigureAwait(false);
            imported.Add(slug);
        }

        return new AgentTemplateImportResult(imported, skippedExisting, unknown);
    }

    private static AgentDefinitionInput ToInput(AgentTemplate template)
    {
        // Imported agents are plain chat personas: the body is seeded verbatim, no tools are granted, and there is no
        // orchestration. The operator grants node tools afterward through the existing tool selector.
        return new AgentDefinitionInput(template.Name,
            template.Description,
            template.Instructions,
            ModelProfile: null,
            ReasoningEffort: null,
            AgentDefinitionKind.Single,
            [],
            new Dictionary<string, bool>(StringComparer.Ordinal),
            OrchestrationTopologyJson: null);
    }
}
