namespace XE_Local_AI_Engine.Client.Services.Integrations.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Default <see cref="IIntegrationTriggerService" />. Modelled on the scheduler's definition management: shape
///     validation lives in the endpoint's FluentValidation rules, and only the two checks that need a database or the
///     agent resolver live here.
/// </summary>
internal sealed class IntegrationTriggerService : IIntegrationTriggerService
{
    private const string AgentMissingMessage = "The selected agent no longer exists.";

    private const string CallerManagedMessage =
        "Caller-managed sessions are limited to agents whose tools are read-only, because tool history is not yet persisted.";

    private const string NameConflictMessage = "Another trigger already uses that name.";

    private readonly IAgentDefinitionResolver _agentResolver;
    private readonly IAgentDefinitionStore _agents;
    private readonly ILocalDefaultChatModelResolver _localDefaultResolver;
    private readonly INodeSettingsStore _nodeSettings;
    private readonly TimeProvider _timeProvider;
    private readonly IIntegrationTriggerStore _triggers;

    public IntegrationTriggerService(IIntegrationTriggerStore triggers,
        IAgentDefinitionStore agents,
        IAgentDefinitionResolver agentResolver,
        INodeSettingsStore nodeSettings,
        ILocalDefaultChatModelResolver localDefaultResolver,
        TimeProvider timeProvider)
    {
        _triggers = triggers ?? throw new ArgumentNullException(nameof(triggers));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _agentResolver = agentResolver ?? throw new ArgumentNullException(nameof(agentResolver));
        _nodeSettings = nodeSettings ?? throw new ArgumentNullException(nameof(nodeSettings));
        _localDefaultResolver = localDefaultResolver ?? throw new ArgumentNullException(nameof(localDefaultResolver));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Task<IReadOnlyList<IntegrationTriggerSnapshot>> ListAsync(CancellationToken cancellationToken = default) =>
        _triggers.ListAsync(cancellationToken);

    public Task<IntegrationTriggerSnapshot?> GetAsync(Guid triggerId, CancellationToken cancellationToken = default) =>
        _triggers.GetByIdAsync(triggerId, cancellationToken);

    public async Task<IntegrationTriggerResult> CreateAsync(IntegrationTriggerCreateInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var name = IIntegrationTriggerService.NormalizeName(input.Name);
        var rejection = await RejectTargetAsync(input.TargetAgentDefinitionId, input.SessionPolicy, cancellationToken).ConfigureAwait(false);
        if (rejection is not null)
        {
            return rejection;
        }

        if (await _triggers.GetByNameAsync(name, cancellationToken).ConfigureAwait(false) is not null)
        {
            return new IntegrationTriggerResult(IntegrationTriggerOutcome.NameConflict, Trigger: null, NameConflictMessage);
        }

        try
        {
            var created = await _triggers.CreateAsync(new IntegrationTriggerCreateCommand(Guid.NewGuid(),
                                                          name,
                                                          input.DisplayName.Trim(),
                                                          NormalizeDescription(input.Description),
                                                          input.Enabled,
                                                          input.TargetKind,
                                                          input.TargetAgentDefinitionId,
                                                          input.SessionPolicy,
                                                          input.AcceptedInputKinds),
                                                      cancellationToken)
                                         .ConfigureAwait(false);

            return new IntegrationTriggerResult(IntegrationTriggerOutcome.Saved, created, Message: null);
        }
        catch (DbUpdateException)
        {
            // The read above is not atomic with the insert. The unique index on the name is what actually decides the
            // race, and the loser must learn it lost as a 409 rather than as a 500.
            return new IntegrationTriggerResult(IntegrationTriggerOutcome.NameConflict, Trigger: null, NameConflictMessage);
        }
    }

    public async Task<IntegrationTriggerResult> UpdateAsync(Guid triggerId, IntegrationTriggerUpdateInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var existing = await _triggers.GetByIdAsync(triggerId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return new IntegrationTriggerResult(IntegrationTriggerOutcome.NotFound, Trigger: null, Message: null);
        }

        var rejection = await RejectTargetAsync(input.TargetAgentDefinitionId, input.SessionPolicy, cancellationToken).ConfigureAwait(false);
        if (rejection is not null)
        {
            return rejection;
        }

        var updated = await _triggers.UpdateAsync(new IntegrationTriggerUpdateCommand(triggerId,
                                                      input.ExpectedVersion,
                                                      input.DisplayName.Trim(),
                                                      NormalizeDescription(input.Description),
                                                      input.Enabled,
                                                      input.TargetAgentDefinitionId,
                                                      input.SessionPolicy,
                                                      input.AcceptedInputKinds),
                                                  cancellationToken)
                                     .ConfigureAwait(false);
        if (!updated)
        {
            return new IntegrationTriggerResult(IntegrationTriggerOutcome.VersionConflict,
                Trigger: null,
                "The trigger changed since it was loaded. Reload it and try again.");
        }

        var reloaded = await _triggers.GetByIdAsync(triggerId, cancellationToken).ConfigureAwait(false);
        return reloaded is null
            ? new IntegrationTriggerResult(IntegrationTriggerOutcome.NotFound, Trigger: null, Message: null)
            : new IntegrationTriggerResult(IntegrationTriggerOutcome.Saved, reloaded, Message: null);
    }

    public Task<bool> DeleteAsync(Guid triggerId, CancellationToken cancellationToken = default) =>
        _triggers.DeleteAsync(triggerId, cancellationToken);

    private static string? NormalizeDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    /// <summary>
    ///     The two store-backed checks, in one place because both writes need both. Returns the rejection to send, or
    ///     <see langword="null" /> when the target is usable.
    /// </summary>
    private async Task<IntegrationTriggerResult?> RejectTargetAsync(Guid agentDefinitionId,
        IntegrationSessionPolicy sessionPolicy,
        CancellationToken cancellationToken)
    {
        var definition = await _agents.GetByIdAsync(agentDefinitionId, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            return new IntegrationTriggerResult(IntegrationTriggerOutcome.AgentMissing, Trigger: null, AgentMissingMessage);
        }

        if (sessionPolicy != IntegrationSessionPolicy.CallerManaged)
        {
            // A per-invocation trigger starts fresh every time, so it carries no history that a missing tool call
            // could make wrong. Resolving the offer for it would be a database read that decides nothing.
            return null;
        }

        // The offer is model-gated, so resolving with a null active model would yield a NARROWER tool set than the run
        // and could pass a trigger the accept path then rejects. Resolve against the same effective model the
        // coordinator picks: the agent's pin, else the node's local default (RunSavedAgentHandler's step 2, minus the
        // capacity call).
        var settings = await _nodeSettings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var localDefault = await _localDefaultResolver.ResolveAsync(settings.DefaultModelName, cancellationToken).ConfigureAwait(false);
        var pinned = string.IsNullOrWhiteSpace(definition.ModelProfile) ? null : definition.ModelProfile;
        var effectiveModel = pinned ?? localDefault;

        var resolved = await _agentResolver.ResolveAsync(agentDefinitionId, effectiveModel, cancellationToken: cancellationToken).ConfigureAwait(false);

        // ToolCategory.Unknown is the fail-closed default for an uncategorised tool, so the predicate is
        // "not ReadLocal" rather than "is WriteExecute". Unlike BenchmarkEligibilityPolicy this does NOT also reject
        // RequiresApproval: approval-gated tools stay in the offer and fail loudly at run time (ruling R4-5).
        var offersWriteTools = resolved?.AllowedTools.Any(static tool => tool.Category != ToolCategory.ReadLocal) ?? false;
        return offersWriteTools
            ? new IntegrationTriggerResult(IntegrationTriggerOutcome.SessionPolicyRejected, Trigger: null, CallerManagedMessage)
            : null;
    }
}
