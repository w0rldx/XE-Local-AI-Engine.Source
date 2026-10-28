namespace XE_Local_AI_Engine.Client.Services.Integrations.Implementation;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Default <see cref="IIntegrationTriggerService" />. Modeled on the scheduler's definition management: shape
///     validation lives in the endpoint's FluentValidation rules, and only the two checks that need the database live
///     here.
/// </summary>
internal sealed class IntegrationTriggerService : IIntegrationTriggerService
{
    private const string AgentMissingMessage = "The selected agent no longer exists.";

    private const string NameConflictMessage = "Another trigger already uses that name.";

    private const string OrchestratorMessage =
        "Orchestrator agents cannot be integration trigger targets; external integrations run a single saved agent.";

    private const string GraphMissingMessage = "The selected graph workflow no longer exists.";

    private const string GraphDisabledMessage = "Graph workflows are turned off on this node; turn them on before saving a graph target.";

    private readonly IAgentDefinitionStore _agents;
    private readonly IGraphWorkflowStore _graphs;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly IIntegrationTriggerStore _triggers;

    public IntegrationTriggerService(IIntegrationTriggerStore triggers,
        IAgentDefinitionStore agents,
        IGraphWorkflowStore graphs,
        INodeRuntimeSettings runtimeSettings)
    {
        _triggers = triggers ?? throw new ArgumentNullException(nameof(triggers));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _graphs = graphs ?? throw new ArgumentNullException(nameof(graphs));
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
    }

    public Task<IReadOnlyList<IntegrationTriggerSnapshot>> ListAsync(CancellationToken cancellationToken = default) =>
        _triggers.ListAsync(cancellationToken);

    public Task<IntegrationTriggerSnapshot?> GetAsync(Guid triggerId, CancellationToken cancellationToken = default) =>
        _triggers.GetByIdAsync(triggerId, cancellationToken);

    public async Task<IntegrationTriggerResult> CreateAsync(IntegrationTriggerCreateInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var name = IIntegrationTriggerService.NormalizeName(input.Name);
        var rejection = await RejectTargetAsync(input.TargetKind, input.TargetAgentDefinitionId, input.TargetGraphWorkflowDefinitionId, cancellationToken);
        if (rejection is not null)
        {
            return rejection;
        }

        if (await _triggers.GetByNameAsync(name, cancellationToken) is not null)
        {
            return new IntegrationTriggerResult
            {
                Outcome = IntegrationTriggerOutcome.NameConflict,
                Trigger = null,
                Message = NameConflictMessage
            };
        }

        try
        {
            var created = await _triggers.CreateAsync(new IntegrationTriggerCreateCommand
                {
                    TriggerId = Guid.NewGuid(),
                    Name = name,
                    DisplayName = input.DisplayName.Trim(),
                    Description = NormalizeDescription(input.Description),
                    Enabled = input.Enabled,
                    TargetKind = input.TargetKind,
                    TargetAgentDefinitionId = input.TargetAgentDefinitionId,
                    TargetGraphWorkflowDefinitionId = input.TargetGraphWorkflowDefinitionId,
                    SessionPolicy = input.SessionPolicy,
                    AcceptedInputKinds = input.AcceptedInputKinds
                },
                cancellationToken);

            return new IntegrationTriggerResult
            {
                Outcome = IntegrationTriggerOutcome.Saved,
                Trigger = created,
                Message = null
            };
        }
        catch (IntegrationTriggerNameConflictException)
        {
            // The read above is not atomic with the insert. The unique index on the name is what actually decides the
            // race, and the loser must learn it lost as a 409 rather than as a 500.
            return new IntegrationTriggerResult
            {
                Outcome = IntegrationTriggerOutcome.NameConflict,
                Trigger = null,
                Message = NameConflictMessage
            };
        }
    }

    public async Task<IntegrationTriggerResult> UpdateAsync(Guid triggerId, IntegrationTriggerUpdateInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var existing = await _triggers.GetByIdAsync(triggerId, cancellationToken);
        if (existing is null)
        {
            return new IntegrationTriggerResult
            {
                Outcome = IntegrationTriggerOutcome.NotFound,
                Trigger = null,
                Message = null
            };
        }

        var rejection = await RejectTargetAsync(input.TargetKind, input.TargetAgentDefinitionId, input.TargetGraphWorkflowDefinitionId, cancellationToken);
        if (rejection is not null)
        {
            return rejection;
        }

        var updated = await _triggers.UpdateAsync(new IntegrationTriggerUpdateCommand
            {
                TriggerId = triggerId,
                ExpectedVersion = input.ExpectedVersion,
                DisplayName = input.DisplayName.Trim(),
                Description = NormalizeDescription(input.Description),
                Enabled = input.Enabled,
                TargetKind = input.TargetKind,
                TargetAgentDefinitionId = input.TargetAgentDefinitionId,
                TargetGraphWorkflowDefinitionId = input.TargetGraphWorkflowDefinitionId,
                SessionPolicy = input.SessionPolicy,
                AcceptedInputKinds = input.AcceptedInputKinds
            },
            cancellationToken);
        if (!updated)
        {
            return new IntegrationTriggerResult
            {
                Outcome = IntegrationTriggerOutcome.VersionConflict,
                Trigger = null,
                Message = "The trigger changed since it was loaded. Reload it and try again."
            };
        }

        var reloaded = await _triggers.GetByIdAsync(triggerId, cancellationToken);
        return reloaded is null
            ? new IntegrationTriggerResult
            {
                Outcome = IntegrationTriggerOutcome.NotFound,
                Trigger = null,
                Message = null
            }
            : new IntegrationTriggerResult
            {
                Outcome = IntegrationTriggerOutcome.Saved,
                Trigger = reloaded,
                Message = null
            };
    }

    public Task<bool> DeleteAsync(Guid triggerId, CancellationToken cancellationToken = default) =>
        _triggers.DeleteAsync(triggerId, cancellationToken);

    private static string? NormalizeDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    /// <summary>
    ///     The store-backed checks, in one place because both writes need them. Returns the rejection to send, or
    ///     <see langword="null" /> when the target is usable. The validator has already enforced "exactly the id the kind names".
    /// </summary>
    private async Task<IntegrationTriggerResult?> RejectTargetAsync(IntegrationTargetKind kind,
        Guid? agentDefinitionId,
        Guid? graphWorkflowDefinitionId,
        CancellationToken cancellationToken)
    {
        if (kind == IntegrationTargetKind.Graph)
        {
            return await RejectGraphTargetAsync(graphWorkflowDefinitionId.GetValueOrDefault(), cancellationToken);
        }

        var definition = await _agents.GetByIdAsync(agentDefinitionId.GetValueOrDefault(), cancellationToken);
        if (definition is null)
        {
            return new IntegrationTriggerResult
            {
                Outcome = IntegrationTriggerOutcome.AgentMissing,
                Trigger = null,
                Message = AgentMissingMessage
            };
        }

        // V1 is scoped to a saved SINGLE agent: the coordinator builds no OrchestrationSpec, so an orchestrator saved here would report Completed having run
        // none of its participants. Refused at save AND re-checked in the coordinator, because a definition's Kind can change after the trigger was written.
        if (definition.Kind != AgentDefinitionKind.Single)
        {
            return new IntegrationTriggerResult
            {
                Outcome = IntegrationTriggerOutcome.TargetKindRejected,
                Trigger = null,
                Message = OrchestratorMessage
            };
        }

        return null;
    }

    private async Task<IntegrationTriggerResult?> RejectGraphTargetAsync(Guid definitionId, CancellationToken cancellationToken)
    {
        if (!await _runtimeSettings.GetGraphWorkflowsEnabledAsync(cancellationToken))
        {
            return Rejected(IntegrationTriggerOutcome.GraphWorkflowsDisabled, GraphDisabledMessage);
        }

        try
        {
            _ = await _graphs.GetDefinitionAsync(definitionId, cancellationToken);
            return null;
        }
        catch (GraphWorkflowNotFoundException)
        {
            return Rejected(IntegrationTriggerOutcome.GraphWorkflowMissing, GraphMissingMessage);
        }
    }

    private static IntegrationTriggerResult Rejected(IntegrationTriggerOutcome outcome, string message) =>
        new()
        {
            Outcome = outcome,
            Trigger = null,
            Message = message
        };
}
