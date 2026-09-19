namespace XE_Local_AI_Engine.Tests.Integrations;

using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Trigger CRUD, and the two checks a FluentValidation rule cannot make: the target agent has to exist, and it has
///     to be a single agent rather than an orchestrator. The session policy is no longer one of them —
///     ADR 0008 R6-1 withdrew the caller-managed read-only-tools rule once the session began persisting and replaying
///     its tool history.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class IntegrationTriggerServiceTests
{
    [Test]
    public async Task CreateAsync_NormalizesTheNameAndTrimsTheDisplayFields()
    {
        var harness = new Harness();
        var agentId = harness.SeedAgent();

        var result = await harness.Service.CreateAsync(Input(" Sensor-Feed ", agentId, displayName: "  Sensor feed  ", description: "   "));

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
        _ = await harness.Service.CreateAsync(Input("sensor-feed", agentId));

        var result = await harness.Service.CreateAsync(Input("SENSOR-FEED", agentId));

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
        _ = await harness.Service.CreateAsync(Input("sensor-feed", agentId));
        harness.Triggers.HideNextNameLookup = true;

        var result = await harness.Service.CreateAsync(Input("sensor-feed", agentId));

        AssertEx.Equal(IntegrationTriggerOutcome.NameConflict, result.Outcome);
        AssertEx.Equal(expected: 1, harness.Triggers.Rows.Count);
    }

    [Test]
    public async Task CreateAsync_WhenTheTargetAgentDoesNotExist_ReturnsAgentMissingAndWritesNothing()
    {
        var harness = new Harness();

        var result = await harness.Service.CreateAsync(Input("sensor-feed", Guid.NewGuid()));

        AssertEx.Equal(IntegrationTriggerOutcome.AgentMissing, result.Outcome);
        AssertEx.Contains(result.Message, "no longer exists");
        AssertEx.Empty(harness.Triggers.Rows);
    }

    [Test]
    public async Task CreateAsync_WhenTheTargetAgentIsAnOrchestrator_IsRejectedAndWritesNothing()
    {
        // V1 is scoped to a saved single agent. The coordinator builds no orchestration spec, so an orchestrator
        // saved here would run as a lone agent and report Completed having run none of its participants — a plausible
        // and materially wrong answer, which is worse than a refusal at save time.
        var harness = new Harness();
        var agentId = harness.SeedAgent(kind: AgentDefinitionKind.Orchestrator);

        var result = await harness.Service.CreateAsync(Input("sensor-feed", agentId));

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
        var created = AssertEx.NotNull((await harness.Service.CreateAsync(Input("sensor-feed", single))).Trigger);

        var result = await harness.Service.UpdateAsync(created.Id, new IntegrationTriggerUpdateInput
        {
            ExpectedVersion = created.Version,
            DisplayName = "Sensor feed",
            Description = null,
            Enabled = true,
            TargetAgentDefinitionId = orchestrator,
            SessionPolicy = IntegrationSessionPolicy.PerInvocation,
            AcceptedInputKinds = IntegrationInputKinds.Text
        });

        AssertEx.Equal(IntegrationTriggerOutcome.TargetKindRejected, result.Outcome);
        AssertEx.Equal(single, harness.Triggers.Rows.Single().TargetAgentDefinitionId, "A rejected update leaves the stored target untouched.");
    }

    [Test]
    [Arguments(ToolCategory.ReadLocal)]
    [Arguments(ToolCategory.WriteExecute)]
    [Arguments(ToolCategory.Orchestration)]
    [Arguments(ToolCategory.Network)]
    [Arguments(ToolCategory.Unknown)]
    public async Task CreateAsync_CallerManagedAgainstAnyToolCategory_IsSaved(ToolCategory category)
    {
        // ADR 0008 R6-1. Every one of these categories used to be a 400 for a caller-managed trigger, because the
        // session persisted no tool history and a continued run could not tell an action it had performed from prose
        // describing one. It persists and replays the calls and their results now, so the offer is arranged here only
        // to show that it decides nothing: the save reads the agent's existence and kind, never its tools.
        var harness = new Harness();
        var agentId = harness.SeedAgent(category);

        var result = await harness.Service.CreateAsync(Input("sensor-feed", agentId, sessionPolicy: IntegrationSessionPolicy.CallerManaged));

        AssertEx.Equal(IntegrationTriggerOutcome.Saved, result.Outcome);
        AssertEx.Equal(IntegrationSessionPolicy.CallerManaged, harness.Triggers.Rows.Single().SessionPolicy);
        _ = harness.AgentResolver.DidNotReceiveWithAnyArgs().ResolveAsync(default(Guid?), default);
        _ = harness.AgentResolver.DidNotReceiveWithAnyArgs().ResolveAsync(default(AgentDefinitionRecord)!, default);
    }

    [Test]
    public async Task UpdateAsync_AppliesTheEditAndBumpsTheVersion()
    {
        var harness = new Harness();
        var agentId = harness.SeedAgent();
        var created = AssertEx.NotNull((await harness.Service.CreateAsync(Input("sensor-feed", agentId))).Trigger);

        var result = await harness.Service.UpdateAsync(created.Id,
                                      new IntegrationTriggerUpdateInput
                                      {
                                          ExpectedVersion = created.Version,
                                          DisplayName = "Renamed label",
                                          Description = "notes",
                                          Enabled = false,
                                          TargetAgentDefinitionId = agentId,
                                          SessionPolicy = IntegrationSessionPolicy.PerInvocation,
                                          AcceptedInputKinds = IntegrationInputKinds.Text
                                      });

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
        var created = AssertEx.NotNull((await harness.Service.CreateAsync(Input("sensor-feed", agentId))).Trigger);

        var result = await harness.Service.UpdateAsync(created.Id,
                                      new IntegrationTriggerUpdateInput
                                      {
                                          ExpectedVersion = created.Version + 7,
                                          DisplayName = "Renamed label",
                                          Description = null,
                                          Enabled = true,
                                          TargetAgentDefinitionId = agentId,
                                          SessionPolicy = IntegrationSessionPolicy.PerInvocation,
                                          AcceptedInputKinds = IntegrationInputKinds.Text
                                      });

        AssertEx.Equal(IntegrationTriggerOutcome.VersionConflict, result.Outcome);
    }

    [Test]
    public async Task UpdateAsync_WithAnUnknownId_ReturnsNotFoundBeforeTouchingTheAgentStore()
    {
        var harness = new Harness();

        var result = await harness.Service.UpdateAsync(Guid.NewGuid(),
                                      new IntegrationTriggerUpdateInput
                                      {
                                          ExpectedVersion = 1,
                                          DisplayName = "Label",
                                          Description = null,
                                          Enabled = true,
                                          TargetAgentDefinitionId = Guid.NewGuid(),
                                          SessionPolicy = IntegrationSessionPolicy.PerInvocation,
                                          AcceptedInputKinds = IntegrationInputKinds.Text
                                      });

        AssertEx.Equal(IntegrationTriggerOutcome.NotFound, result.Outcome);
    }

    [Test]
    public async Task UpdateAsync_SwitchingToCallerManagedAgainstAWriteAgent_IsSaved()
    {
        // The update half of R6-1: switching a live trigger onto a caller-managed session is an ordinary edit now.
        var harness = new Harness();
        var agentId = harness.SeedAgent(ToolCategory.WriteExecute);
        var created = AssertEx.NotNull((await harness.Service.CreateAsync(Input("sensor-feed", agentId))).Trigger);

        var result = await harness.Service.UpdateAsync(created.Id,
                                      new IntegrationTriggerUpdateInput
                                      {
                                          ExpectedVersion = created.Version,
                                          DisplayName = "Label",
                                          Description = null,
                                          Enabled = true,
                                          TargetAgentDefinitionId = agentId,
                                          SessionPolicy = IntegrationSessionPolicy.CallerManaged,
                                          AcceptedInputKinds = IntegrationInputKinds.Text
                                      });

        AssertEx.Equal(IntegrationTriggerOutcome.Saved, result.Outcome);
        AssertEx.Equal(IntegrationSessionPolicy.CallerManaged, harness.Triggers.Rows.Single().SessionPolicy);
    }

    [Test]
    public async Task ListAndGetAndDelete_RoundTrip()
    {
        var harness = new Harness();
        var agentId = harness.SeedAgent();
        var created = AssertEx.NotNull((await harness.Service.CreateAsync(Input("sensor-feed", agentId))).Trigger);

        AssertEx.Equal(created.Id, AssertEx.NotNull(await harness.Service.GetAsync(created.Id)).Id);
        AssertEx.Equal(expected: 1, (await harness.Service.ListAsync()).Count);
        AssertEx.True(await harness.Service.DeleteAsync(created.Id));
        AssertEx.False(await harness.Service.DeleteAsync(created.Id));
        AssertEx.Null(await harness.Service.GetAsync(created.Id));
    }

    private static IntegrationTriggerCreateInput Input(string name,
        Guid agentDefinitionId,
        string displayName = "Sensor feed",
        string? description = null,
        IntegrationSessionPolicy sessionPolicy = IntegrationSessionPolicy.PerInvocation) =>
        new()
        {
            Name = name,
            DisplayName = displayName,
            Description = description,
            Enabled = true,
            TargetKind = IntegrationTargetKind.Agent,
            TargetAgentDefinitionId = agentDefinitionId,
            SessionPolicy = sessionPolicy,
            AcceptedInputKinds = IntegrationInputKinds.Text | IntegrationInputKinds.Json
        };

    private sealed class Harness
    {
        public Harness()
        {
            Agents = Substitute.For<IAgentDefinitionStore>();
            AgentResolver = Substitute.For<IAgentDefinitionResolver>();

            Service = new IntegrationTriggerService(Triggers, Agents, new ManualTimeProvider());
        }

        /// <summary>
        ///     The resolver the service NO LONGER injects. It is kept so a suite can still arrange an offer and assert
        ///     the save never reads it, which is what ADR 0008 R6-1 withdrew.
        /// </summary>

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
                      .Returns(new AgentDefinitionRecord
                      {
                          Id = agentId,
                          Name = "Agent",
                          Description = null,
                          Instructions = "Instructions",
                          ModelProfile = modelProfile,
                          ReasoningEffort = null,
                          Kind = kind,
                          AllowedToolNames = [],
                          ToolApprovals = new Dictionary<string, bool>(StringComparer.Ordinal),
                          OrchestrationTopologyJson = null,
                          Version = 1,
                          CreatedAtUtc = 1,
                          UpdatedAtUtc = 1
                      });

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
