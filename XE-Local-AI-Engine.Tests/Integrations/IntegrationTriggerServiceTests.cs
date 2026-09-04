namespace XE_Local_AI_Engine.Tests.Integrations;

using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Trigger CRUD, and the two checks a FluentValidation rule cannot make: the target agent has to exist, and a
///     caller-managed trigger's agent has to offer read-only tools only (ruling R4-9(a)). The caller-managed check is a
///     PREFLIGHT — the accept path repeats it and is the authority — so its job here is to stop an operator saving a
///     configuration that can never run.
/// </summary>
public sealed class IntegrationTriggerServiceTests
{
    [Test]
    public async Task CreateAsync_NormalizesTheNameAndTrimsTheDisplayFields()
    {
        var harness = new Harness();
        var agentId = harness.SeedAgent();

        var result = await harness.Service.CreateAsync(Input(" Sensor-Feed ", agentId, displayName: "  Sensor feed  ", description: "   ")).ConfigureAwait(false);

        AssertEx.Equal(IntegrationTriggerOutcome.Saved, result.Outcome);
        var trigger = AssertEx.NotNull(result.Trigger);
        AssertEx.Equal("sensor-feed", trigger.Name, "The external name is lowercased and trimmed so the UI and a curl command resolve the same row.");
        AssertEx.Equal("Sensor feed", trigger.DisplayName);
        AssertEx.Null(trigger.Description, "A whitespace-only description is stored as absent, not as blanks.");
    }

    [Test]
    public async Task CreateAsync_WhenTheNameIsAlreadyTaken_ReturnsNameConflictAndWritesNothing()
    {
        var harness = new Harness();
        var agentId = harness.SeedAgent();
        _ = await harness.Service.CreateAsync(Input("sensor-feed", agentId)).ConfigureAwait(false);

        var result = await harness.Service.CreateAsync(Input("SENSOR-FEED", agentId)).ConfigureAwait(false);

        AssertEx.Equal(IntegrationTriggerOutcome.NameConflict, result.Outcome);
        AssertEx.Equal(expected: 1, harness.Triggers.Rows.Count);
    }

    [Test]
    public async Task CreateAsync_WhenTheUniqueIndexDecidesTheRace_StillReturnsNameConflict()
    {
        // The pre-check and the insert are not atomic. The index is what resolves two simultaneous creates, and the
        // loser must learn it lost as a 409 rather than as a 500.
        var harness = new Harness();
        var agentId = harness.SeedAgent();
        _ = await harness.Service.CreateAsync(Input("sensor-feed", agentId)).ConfigureAwait(false);
        harness.Triggers.HideNextNameLookup = true;

        var result = await harness.Service.CreateAsync(Input("sensor-feed", agentId)).ConfigureAwait(false);

        AssertEx.Equal(IntegrationTriggerOutcome.NameConflict, result.Outcome);
        AssertEx.Equal(expected: 1, harness.Triggers.Rows.Count);
    }

    [Test]
    public async Task CreateAsync_WhenTheTargetAgentDoesNotExist_ReturnsAgentMissingAndWritesNothing()
    {
        var harness = new Harness();

        var result = await harness.Service.CreateAsync(Input("sensor-feed", Guid.NewGuid())).ConfigureAwait(false);

        AssertEx.Equal(IntegrationTriggerOutcome.AgentMissing, result.Outcome);
        AssertEx.Contains(result.Message, "no longer exists");
        AssertEx.Empty(harness.Triggers.Rows);
    }

    [Test]
    public async Task CreateAsync_WhenTheTargetAgentIsAnOrchestrator_IsRejectedAndWritesNothing()
    {
        // Ruling D2 scopes V1 to a saved single agent. The coordinator builds no orchestration spec, so an orchestrator
        // saved here would run as a lone agent and report Completed having run none of its participants — a plausible
        // and materially wrong answer, which is worse than a refusal at save time.
        var harness = new Harness();
        var agentId = harness.SeedAgent(kind: AgentDefinitionKind.Orchestrator);

        var result = await harness.Service.CreateAsync(Input("sensor-feed", agentId)).ConfigureAwait(false);

        AssertEx.Equal(IntegrationTriggerOutcome.TargetKindRejected, result.Outcome);
        AssertEx.Contains(result.Message, "Orchestrator");
        AssertEx.Empty(harness.Triggers.Rows);
    }

    [Test]
    public async Task UpdateAsync_WhenRepointedAtAnOrchestrator_IsRejectedAndLeavesTheRowAlone()
    {
        var harness = new Harness();
        var single = harness.SeedAgent();
        var orchestrator = harness.SeedAgent(kind: AgentDefinitionKind.Orchestrator);
        var created = AssertEx.NotNull((await harness.Service.CreateAsync(Input("sensor-feed", single)).ConfigureAwait(false)).Trigger);

        var result = await harness.Service.UpdateAsync(created.Id, new IntegrationTriggerUpdateInput(created.Version,
                                            "Sensor feed",
                                            Description: null,
                                            Enabled: true,
                                            orchestrator,
                                            IntegrationSessionPolicy.PerInvocation,
                                            IntegrationInputKinds.Text)).ConfigureAwait(false);

        AssertEx.Equal(IntegrationTriggerOutcome.TargetKindRejected, result.Outcome);
        AssertEx.Equal(single, harness.Triggers.Rows.Single().TargetAgentDefinitionId, "A rejected update leaves the stored target untouched.");
    }

    [Test]
    public async Task CreateAsync_CallerManagedAgainstAReadLocalAgent_IsAccepted()
    {
        var harness = new Harness();
        var agentId = harness.SeedAgent(ToolCategory.ReadLocal);

        var result = await harness.Service.CreateAsync(Input("sensor-feed", agentId, sessionPolicy: IntegrationSessionPolicy.CallerManaged)).ConfigureAwait(false);

        AssertEx.Equal(IntegrationTriggerOutcome.Saved, result.Outcome);
    }

    [Test]
    public async Task CreateAsync_CallerManagedAgainstAReadLocalAgentThatNeedsApproval_IsStillAccepted()
    {
        // Ruling R4-5 keeps approval-gated tools in the offer, so the predicate copies BenchmarkEligibilityPolicy's
        // category half and NOT its RequiresApproval half. An approval-gated read-only tool is a legal target.
        var harness = new Harness();
        var agentId = harness.SeedAgent(ToolCategory.ReadLocal, requiresApproval: true);

        var result = await harness.Service.CreateAsync(Input("sensor-feed", agentId, sessionPolicy: IntegrationSessionPolicy.CallerManaged)).ConfigureAwait(false);

        AssertEx.Equal(IntegrationTriggerOutcome.Saved, result.Outcome);
    }

    [Test]
    [Arguments(ToolCategory.WriteExecute)]
    [Arguments(ToolCategory.Orchestration)]
    [Arguments(ToolCategory.Network)]
    [Arguments(ToolCategory.Unknown)]
    public async Task CreateAsync_CallerManagedAgainstANonReadLocalAgent_IsRejected(ToolCategory category)
    {
        // Unknown is the fail-closed default for an uncategorised tool, so it must be rejected exactly like an
        // actuator: "not ReadLocal" is the predicate, never "is WriteExecute".
        var harness = new Harness();
        var agentId = harness.SeedAgent(category);

        var result = await harness.Service.CreateAsync(Input("sensor-feed", agentId, sessionPolicy: IntegrationSessionPolicy.CallerManaged)).ConfigureAwait(false);

        AssertEx.Equal(IntegrationTriggerOutcome.SessionPolicyRejected, result.Outcome);
        AssertEx.Contains(result.Message, "read-only");
        AssertEx.Empty(harness.Triggers.Rows);
    }

    [Test]
    public async Task CreateAsync_PerInvocationAgainstAWriteAgent_IsAcceptedAndNeverResolvesTheOffer()
    {
        // A per-invocation trigger starts fresh every time, so there is no transcript for a missing tool call to be
        // wrong about — and resolving the offer for it would be a read that decides nothing.
        var harness = new Harness();
        var agentId = harness.SeedAgent(ToolCategory.WriteExecute);

        var result = await harness.Service.CreateAsync(Input("sensor-feed", agentId)).ConfigureAwait(false);

        AssertEx.Equal(IntegrationTriggerOutcome.Saved, result.Outcome);
        _ = harness.AgentResolver.DidNotReceiveWithAnyArgs().ResolveAsync(default, default);
    }

    [Test]
    public async Task CreateAsync_ResolvesTheOfferAgainstThePinnedModelThenTheLocalDefault()
    {
        // Resolving with a null active model would withhold every tool and pass a trigger the accept path then
        // rejects, so the save-time check has to use the same effective model the coordinator would pick.
        var harness = new Harness();
        var pinned = harness.SeedAgent(ToolCategory.ReadLocal, modelProfile: "pinned-model");
        var unpinned = harness.SeedAgent(ToolCategory.ReadLocal);

        _ = await harness.Service.CreateAsync(Input("pinned", pinned, sessionPolicy: IntegrationSessionPolicy.CallerManaged)).ConfigureAwait(false);
        _ = await harness.Service.CreateAsync(Input("unpinned", unpinned, sessionPolicy: IntegrationSessionPolicy.CallerManaged)).ConfigureAwait(false);

        _ = harness.AgentResolver.Received().ResolveAsync(pinned, "pinned-model", cancellationToken: Arg.Any<CancellationToken>());
        _ = harness.AgentResolver.Received().ResolveAsync(unpinned, Harness.LocalDefaultModel, cancellationToken: Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateAsync_AppliesTheEditAndBumpsTheVersion()
    {
        var harness = new Harness();
        var agentId = harness.SeedAgent();
        var created = AssertEx.NotNull((await harness.Service.CreateAsync(Input("sensor-feed", agentId)).ConfigureAwait(false)).Trigger);

        var result = await harness.Service.UpdateAsync(created.Id,
                                        new IntegrationTriggerUpdateInput(created.Version,
                                            "Renamed label",
                                            "notes",
                                            Enabled: false,
                                            agentId,
                                            IntegrationSessionPolicy.PerInvocation,
                                            IntegrationInputKinds.Text))
                                   .ConfigureAwait(false);

        AssertEx.Equal(IntegrationTriggerOutcome.Saved, result.Outcome);
        var updated = AssertEx.NotNull(result.Trigger);
        AssertEx.Equal("sensor-feed", updated.Name, "The external name is not editable: renaming a live trigger is a delete-and-create decision.");
        AssertEx.Equal("Renamed label", updated.DisplayName);
        AssertEx.False(updated.Enabled);
        AssertEx.Equal(IntegrationInputKinds.Text, updated.AcceptedInputKinds);
        AssertEx.Equal(created.Version + 1, updated.Version);
    }

    [Test]
    public async Task UpdateAsync_WithAStaleVersion_ReturnsVersionConflict()
    {
        var harness = new Harness();
        var agentId = harness.SeedAgent();
        var created = AssertEx.NotNull((await harness.Service.CreateAsync(Input("sensor-feed", agentId)).ConfigureAwait(false)).Trigger);

        var result = await harness.Service.UpdateAsync(created.Id,
                                        new IntegrationTriggerUpdateInput(created.Version + 7,
                                            "Renamed label",
                                            Description: null,
                                            Enabled: true,
                                            agentId,
                                            IntegrationSessionPolicy.PerInvocation,
                                            IntegrationInputKinds.Text))
                                   .ConfigureAwait(false);

        AssertEx.Equal(IntegrationTriggerOutcome.VersionConflict, result.Outcome);
    }

    [Test]
    public async Task UpdateAsync_WithAnUnknownId_ReturnsNotFoundBeforeTouchingTheAgentStore()
    {
        var harness = new Harness();

        var result = await harness.Service.UpdateAsync(Guid.NewGuid(),
                                        new IntegrationTriggerUpdateInput(ExpectedVersion: 1,
                                            "Label",
                                            Description: null,
                                            Enabled: true,
                                            Guid.NewGuid(),
                                            IntegrationSessionPolicy.PerInvocation,
                                            IntegrationInputKinds.Text))
                                   .ConfigureAwait(false);

        AssertEx.Equal(IntegrationTriggerOutcome.NotFound, result.Outcome);
    }

    [Test]
    public async Task UpdateAsync_SwitchingToCallerManagedAgainstAWriteAgent_IsRejected()
    {
        var harness = new Harness();
        var agentId = harness.SeedAgent(ToolCategory.WriteExecute);
        var created = AssertEx.NotNull((await harness.Service.CreateAsync(Input("sensor-feed", agentId)).ConfigureAwait(false)).Trigger);

        var result = await harness.Service.UpdateAsync(created.Id,
                                        new IntegrationTriggerUpdateInput(created.Version,
                                            "Label",
                                            Description: null,
                                            Enabled: true,
                                            agentId,
                                            IntegrationSessionPolicy.CallerManaged,
                                            IntegrationInputKinds.Text))
                                   .ConfigureAwait(false);

        AssertEx.Equal(IntegrationTriggerOutcome.SessionPolicyRejected, result.Outcome);
    }

    [Test]
    public async Task ListAndGetAndDelete_RoundTrip()
    {
        var harness = new Harness();
        var agentId = harness.SeedAgent();
        var created = AssertEx.NotNull((await harness.Service.CreateAsync(Input("sensor-feed", agentId)).ConfigureAwait(false)).Trigger);

        AssertEx.Equal(created.Id, AssertEx.NotNull(await harness.Service.GetAsync(created.Id).ConfigureAwait(false)).Id);
        AssertEx.Equal(expected: 1, (await harness.Service.ListAsync().ConfigureAwait(false)).Count);
        AssertEx.True(await harness.Service.DeleteAsync(created.Id).ConfigureAwait(false));
        AssertEx.False(await harness.Service.DeleteAsync(created.Id).ConfigureAwait(false));
        AssertEx.Null(await harness.Service.GetAsync(created.Id).ConfigureAwait(false));
    }

    private static IntegrationTriggerCreateInput Input(string name,
        Guid agentDefinitionId,
        string displayName = "Sensor feed",
        string? description = null,
        IntegrationSessionPolicy sessionPolicy = IntegrationSessionPolicy.PerInvocation) =>
        new(name,
            displayName,
            description,
            Enabled: true,
            IntegrationTargetKind.Agent,
            agentDefinitionId,
            sessionPolicy,
            IntegrationInputKinds.Text | IntegrationInputKinds.Json);

    private sealed class Harness
    {
        public const string LocalDefaultModel = "local-default-model";

        public Harness()
        {
            Agents = Substitute.For<IAgentDefinitionStore>();
            AgentResolver = Substitute.For<IAgentDefinitionResolver>();
            var nodeSettings = Substitute.For<INodeSettingsStore>();
            _ = nodeSettings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
            var localDefault = Substitute.For<ILocalDefaultChatModelResolver>();
            _ = localDefault.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(LocalDefaultModel);

            Service = new IntegrationTriggerService(Triggers, Agents, AgentResolver, nodeSettings, localDefault, new ManualTimeProvider());
        }

        public IAgentDefinitionResolver AgentResolver { get; }

        public IAgentDefinitionStore Agents { get; }

        public FakeIntegrationTriggerStore Triggers { get; } = new();

        public IIntegrationTriggerService Service { get; }

        /// <summary>Seeds an agent definition whose resolved offer carries exactly one tool of <paramref name="category" />.</summary>
        public Guid SeedAgent(ToolCategory category = ToolCategory.ReadLocal,
            bool requiresApproval = false,
            string? modelProfile = null,
            AgentDefinitionKind kind = AgentDefinitionKind.Single)
        {
            var agentId = Guid.NewGuid();
            _ = Agents.GetByIdAsync(agentId, Arg.Any<CancellationToken>())
                      .Returns(new AgentDefinitionRecord(agentId,
                          "Agent",
                          Description: null,
                          "Instructions",
                          modelProfile,
                          ReasoningEffort: null,
                          kind,
                          [],
                          new Dictionary<string, bool>(StringComparer.Ordinal),
                          OrchestrationTopologyJson: null,
                          Version: 1,
                          CreatedAtUtc: 1,
                          UpdatedAtUtc: 1));

            _ = AgentResolver.ResolveAsync(agentId,
                                 Arg.Any<string?>(),
                                 Arg.Any<string?>(),
                                 Arg.Any<bool>(),
                                 Arg.Any<bool>(),
                                 Arg.Any<bool>(),
                                 Arg.Any<CancellationToken>())
                             .Returns(new ResolvedAgentRuntime("prompt",
                                 [
                                     new AllowedToolDto
                                     {
                                         Id = Guid.NewGuid(),
                                         Name = "probe_tool",
                                         Location = ToolLocation.ApiSide,
                                         Category = category,
                                         RequiresApproval = requiresApproval
                                     }
                                 ],
                                 modelProfile,
                                 ReasoningEffort: null,
                                 AgentDefinitionVersion: 1));

            return agentId;
        }
    }
}
