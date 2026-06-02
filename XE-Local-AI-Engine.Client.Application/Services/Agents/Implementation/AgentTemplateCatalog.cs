namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using System.Text.Json;

/// <summary>
///     Loads the curated starter-pack templates from the embedded <c>agent-templates.seed.json</c> resource once and
///     serves them from an in-memory cache. The resource is committed in-repo (transformed once at build time from the
///     vendored agency-agents source), so the catalog never reaches the network — the whole point of vendoring.
/// </summary>
internal sealed class AgentTemplateCatalog : IAgentTemplateCatalog
{
    private const string ResourceNameSuffix = "agent-templates.seed.json";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IReadOnlyList<AgentTemplate> _templates;
    private readonly IReadOnlyDictionary<string, AgentTemplate> _bySlug;

    public AgentTemplateCatalog()
    {
        _templates = LoadTemplates();
        _bySlug = _templates.ToDictionary(template => template.Slug, StringComparer.Ordinal);
    }

    public IReadOnlyList<AgentTemplate> List()
    {
        return _templates;
    }

    public AgentTemplate? TryGet(string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        return _bySlug.TryGetValue(slug, out var template) ? template : null;
    }

    private static IReadOnlyList<AgentTemplate> LoadTemplates()
    {
        var assembly = typeof(AgentTemplateCatalog).Assembly;

        // Match by suffix so the catalog is robust to the assembly's manifest-resource-name prefix (root namespace +
        // folder path), which can drift as the project moves.
        var resourceName = assembly.GetManifestResourceNames()
                                   .FirstOrDefault(name => name.EndsWith(ResourceNameSuffix, StringComparison.Ordinal))
                           ?? throw new InvalidOperationException($"Embedded resource '{ResourceNameSuffix}' was not found in assembly '{assembly.GetName().Name}'.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' could not be opened.");

        var document = JsonSerializer.Deserialize<AgentTemplateSeedDocument>(stream, SerializerOptions)
                       ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' deserialized to null.");

        return [.. document.Templates];
    }

    /// <summary>Wire shape of the seed file: a provenance header plus the template array. Only the templates are used.</summary>
    private sealed record AgentTemplateSeedDocument(IReadOnlyList<AgentTemplate> Templates);
}
