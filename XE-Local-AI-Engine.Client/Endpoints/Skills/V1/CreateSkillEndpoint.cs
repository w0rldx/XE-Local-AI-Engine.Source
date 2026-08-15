namespace XE_Local_AI_Engine.Client.Endpoints.Skills.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Skills.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class CreateSkillEndpoint(IAgentSkillService agentSkillService, TimeProvider timeProvider)
    : Endpoint<CreateSkillRequest, SkillResponse>
{
    private readonly IAgentSkillService _agentSkillService = agentSkillService ?? throw new ArgumentNullException(nameof(agentSkillService));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public override void Configure()
    {
        Post(LocalApiRoutes.Skills.Definitions);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CreateSkillRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // The echoed provenance block is operator input like any other field, so it is bounded here, not trusted.
        if (GenerationProvenance.Validate(req.GenerationMetadata) is { } metadataError)
        {
            AddError(metadataError);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var record = await _agentSkillService.CreateAsync(req.ToInput(_timeProvider.GetUtcNow()), ct).ConfigureAwait(false);
            await Send.CreatedAtAsync<GetSkillEndpoint>(new
                {
                    skillId = record.Id
                },
                record.ToResponse(),
                cancellation: ct).ConfigureAwait(false);
        }
        catch (AgentSkillValidationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
