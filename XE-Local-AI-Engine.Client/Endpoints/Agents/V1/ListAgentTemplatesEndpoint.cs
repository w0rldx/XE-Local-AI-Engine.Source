namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class ListAgentTemplatesEndpoint(IAgentTemplateCatalog catalog, IAgentDefinitionService agentDefinitions)
    : EndpointWithoutRequest<ListAgentTemplatesResponse>
{
    private readonly IAgentTemplateCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly IAgentDefinitionService _agentDefinitions = agentDefinitions ?? throw new ArgumentNullException(nameof(agentDefinitions));

    public override void Configure()
    {
        Get(LocalApiRoutes.Agents.Templates);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var alreadySeeded = await _agentDefinitions.ListSeededSlugsAsync(ct).ConfigureAwait(false);

        var items = _catalog.List()
                            .Select(template => new AgentTemplateSummary
                            {
                                Slug = template.Slug,
                                Name = template.Name,
                                Description = template.Description,
                                Division = template.Division,
                                EstimatedPromptTokens = template.EstimatedPromptTokens,
                                HasOriginalTools = template.OriginalTools.Count > 0,
                                AlreadyImported = alreadySeeded.Contains(template.Slug)
                            })
                            .ToArray();

        await Send.OkAsync(new ListAgentTemplatesResponse
            {
                Items = items
            },
            ct).ConfigureAwait(false);
    }
}
