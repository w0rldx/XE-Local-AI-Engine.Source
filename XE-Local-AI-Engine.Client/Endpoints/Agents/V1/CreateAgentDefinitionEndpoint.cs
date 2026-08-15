namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class CreateAgentDefinitionEndpoint(IAgentDefinitionService agentDefinitionService, TimeProvider timeProvider)
    : Endpoint<CreateAgentDefinitionRequest, AgentDefinitionResponse>
{
    private readonly IAgentDefinitionService _agentDefinitionService = agentDefinitionService ?? throw new ArgumentNullException(nameof(agentDefinitionService));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public override void Configure()
    {
        Post(LocalApiRoutes.Agents.Definitions);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CreateAgentDefinitionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // The echoed provenance block is operator input like any other field, so it is bounded here, not trusted.
        if (GenerationProvenance.Validate(req.GenerationMetadata) is { } metadataError)
        {
            AddError(metadataError);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var record = await _agentDefinitionService.CreateAsync(req.ToInput(_timeProvider.GetUtcNow()), ct).ConfigureAwait(false);
        await Send.CreatedAtAsync<GetAgentDefinitionEndpoint>(new
            {
                agentDefinitionId = record.Id
            },
            record.ToResponse(),
            cancellation: ct).ConfigureAwait(false);
    }
}
