namespace XE_Local_AI_Engine.Tests.Agents;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class AgentDefinitionServiceTests
{
    [Test]
    public async Task CreateAsync_WithValidInput_PersistsThroughStore()
    {
        var service = CreateService(out var store, ["GetCurrentTime"]);
        var input = CreateInput(allowedTools: ["GetCurrentTime"]);
        var stored = CreateRecord(input);
        store.AddAsync(input, Arg.Any<CancellationToken>()).Returns(stored);

        var result = await service.CreateAsync(input).ConfigureAwait(false);

        AssertEx.Equal(stored.Id, result.Id);
        await store.Received(1).AddAsync(input, Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WithEmptyName_ThrowsValidation()
    {
        var service = CreateService(out var store);
        var input = CreateInput("   ");

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.CreateAsync(input)).ConfigureAwait(false);
        await store.DidNotReceive().AddAsync(Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WithEmptyInstructions_ThrowsValidation()
    {
        var service = CreateService(out var store);
        var input = CreateInput(instructions: "");

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.CreateAsync(input)).ConfigureAwait(false);
        await store.DidNotReceive().AddAsync(Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WithApprovalKeyOutsideAllowedTools_ThrowsValidation()
    {
        var service = CreateService(out _, ["GetCurrentTime"]);
        var input = CreateInput(allowedTools: ["GetCurrentTime"],
            toolApprovals: new Dictionary<string, bool>
            {
                ["NotAllowed"] = true
            });

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.CreateAsync(input)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WithInvalidReasoningEffort_ThrowsValidation()
    {
        var service = CreateService(out _);
        var input = CreateInput(reasoningEffort: "turbo");

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.CreateAsync(input)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WithUnknownToolName_DoesNotThrow_AndPersists()
    {
        // An unknown tool name is a warning, not a failure — a tool can be reinstalled later. Persistence proceeds.
        var service = CreateService(out var store, ["GetCurrentTime"]);
        var input = CreateInput(allowedTools: ["GetCurrentTime", "MaybeLaterTool"]);
        store.AddAsync(input, Arg.Any<CancellationToken>()).Returns(CreateRecord(input));

        var result = await service.CreateAsync(input).ConfigureAwait(false);

        AssertEx.NotNull(result);
        await store.Received(1).AddAsync(input, Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task UpdateAsync_WhenStoreReturnsNull_ReturnsNull()
    {
        var service = CreateService(out var store, ["GetCurrentTime"]);
        var id = Guid.NewGuid();
        var input = CreateInput(allowedTools: ["GetCurrentTime"]);
        store.UpdateAsync(id, input, Arg.Any<CancellationToken>()).Returns((AgentDefinitionRecord?)null);

        var result = await service.UpdateAsync(id, input).ConfigureAwait(false);

        AssertEx.True(result is null, "Updating a missing definition must return null.");
    }

    [Test]
    public async Task UpdateAsync_WithInvalidInput_ThrowsBeforeStore()
    {
        var service = CreateService(out var store);
        var input = CreateInput("");

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.UpdateAsync(Guid.NewGuid(), input)).ConfigureAwait(false);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task DeleteGetList_DelegateToStore()
    {
        var service = CreateService(out var store, ["GetCurrentTime"]);
        var id = Guid.NewGuid();
        var record = CreateRecord(CreateInput(allowedTools: ["GetCurrentTime"]));
        store.DeleteAsync(id, Arg.Any<CancellationToken>()).Returns(true);
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(record);
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([record]);

        AssertEx.Equal(expected: true, await service.DeleteAsync(id).ConfigureAwait(false));
        AssertEx.Equal(record.Id, (await service.GetByIdAsync(id).ConfigureAwait(false))!.Id);
        AssertEx.Equal(expected: 1, (await service.ListAsync().ConfigureAwait(false)).Count);
    }

    [Test]
    public async Task CreateAsync_WithValidOrchestratorTopology_PersistsThroughStore()
    {
        var service = CreateService(out var store);
        var triage = Guid.NewGuid();
        var specialist = Guid.NewGuid();
        var input = CreateInput(kind: AgentDefinitionKind.Orchestrator,
            orchestrationTopologyJson: TopologyJson(triage,
                [triage, specialist],
                [
                    new OrchestrationHandoff
                    {
                        FromAgentDefinitionId = triage,
                        ToAgentDefinitionId = specialist
                    }
                ]));
        // Both participants exist → no warning, no failure.
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([StoredRecord(triage), StoredRecord(specialist)]);
        store.AddAsync(input, Arg.Any<CancellationToken>()).Returns(CreateRecord(input));

        var result = await service.CreateAsync(input).ConfigureAwait(false);

        AssertEx.NotNull(result);
        await store.Received(1).AddAsync(input, Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WithMeshOrchestratorTopology_NoHandoffsRequired_Persists()
    {
        // An empty handoff list means "mesh default" (MAF auto-wires); it is valid, not a failure.
        var service = CreateService(out var store);
        var triage = Guid.NewGuid();
        var specialist = Guid.NewGuid();
        var input = CreateInput(kind: AgentDefinitionKind.Orchestrator,
            orchestrationTopologyJson: TopologyJson(triage, [triage, specialist]));
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([StoredRecord(triage), StoredRecord(specialist)]);
        store.AddAsync(input, Arg.Any<CancellationToken>()).Returns(CreateRecord(input));

        var result = await service.CreateAsync(input).ConfigureAwait(false);

        AssertEx.NotNull(result);
        await store.Received(1).AddAsync(input, Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_OrchestratorWithoutTopology_ThrowsValidation()
    {
        var service = CreateService(out var store);
        var input = CreateInput(kind: AgentDefinitionKind.Orchestrator, orchestrationTopologyJson: null);

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.CreateAsync(input)).ConfigureAwait(false);
        await store.DidNotReceive().AddAsync(Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_OrchestratorWithMalformedTopologyJson_ThrowsValidation()
    {
        var service = CreateService(out var store);
        var input = CreateInput(kind: AgentDefinitionKind.Orchestrator, orchestrationTopologyJson: "{ not valid json ");

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.CreateAsync(input)).ConfigureAwait(false);
        await store.DidNotReceive().AddAsync(Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_OrchestratorWithUnsupportedVersion_ThrowsValidation()
    {
        // A version this build does not understand parses to null in the shared parser → authoring rejects it.
        var service = CreateService(out var store);
        var triage = Guid.NewGuid();
        var specialist = Guid.NewGuid();
        var json = $$"""{"version":99,"triageAgentDefinitionId":"{{triage}}","participantAgentDefinitionIds":["{{triage}}","{{specialist}}"],"handoffs":[]}""";
        var input = CreateInput(kind: AgentDefinitionKind.Orchestrator, orchestrationTopologyJson: json);

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.CreateAsync(input)).ConfigureAwait(false);
        await store.DidNotReceive().AddAsync(Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_OrchestratorWithFewerThanTwoParticipants_ThrowsValidation()
    {
        var service = CreateService(out var store);
        var triage = Guid.NewGuid();
        var input = CreateInput(kind: AgentDefinitionKind.Orchestrator,
            orchestrationTopologyJson: TopologyJson(triage, [triage]));

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.CreateAsync(input)).ConfigureAwait(false);
        await store.DidNotReceive().AddAsync(Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_OrchestratorTriageNotInParticipants_ThrowsValidation()
    {
        var service = CreateService(out var store);
        var triage = Guid.NewGuid();
        var input = CreateInput(kind: AgentDefinitionKind.Orchestrator,
            orchestrationTopologyJson: TopologyJson(triage, [Guid.NewGuid(), Guid.NewGuid()]));

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.CreateAsync(input)).ConfigureAwait(false);
        await store.DidNotReceive().AddAsync(Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_OrchestratorHandoffEdgeToUnknownParticipant_ThrowsValidation()
    {
        var service = CreateService(out var store);
        var triage = Guid.NewGuid();
        var specialist = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var input = CreateInput(kind: AgentDefinitionKind.Orchestrator,
            orchestrationTopologyJson: TopologyJson(triage,
                [triage, specialist],
                [
                    new OrchestrationHandoff
                    {
                        FromAgentDefinitionId = triage,
                        ToAgentDefinitionId = stranger
                    }
                ]));

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.CreateAsync(input)).ConfigureAwait(false);
        await store.DidNotReceive().AddAsync(Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_OrchestratorWithDeletedParticipant_WarnsButPersists()
    {
        // A participant id that no longer exists in the store is a warning, not a failure — it mirrors the no-FK
        // tolerance and the runtime resolver's degrade. Persistence proceeds.
        var service = CreateService(out var store);
        var triage = Guid.NewGuid();
        var deletedSpecialist = Guid.NewGuid();
        var input = CreateInput(kind: AgentDefinitionKind.Orchestrator,
            orchestrationTopologyJson: TopologyJson(triage, [triage, deletedSpecialist]));
        // Only the triage exists; the specialist was deleted.
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([StoredRecord(triage)]);
        store.AddAsync(input, Arg.Any<CancellationToken>()).Returns(CreateRecord(input));

        var result = await service.CreateAsync(input).ConfigureAwait(false);

        AssertEx.NotNull(result);
        await store.Received(1).AddAsync(input, Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_SingleAgentWithTopologyPayload_ThrowsValidation()
    {
        // A single agent must not carry a topology; a stray payload would be silently ignored at runtime, so reject it.
        var service = CreateService(out var store);
        var input = CreateInput(kind: AgentDefinitionKind.Single,
            orchestrationTopologyJson: TopologyJson(Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()]));

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.CreateAsync(input)).ConfigureAwait(false);
        await store.DidNotReceive().AddAsync(Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task UpdateAsync_WithValidOrchestratorTopology_ValidatesAndDelegates()
    {
        var service = CreateService(out var store);
        var id = Guid.NewGuid();
        var triage = Guid.NewGuid();
        var specialist = Guid.NewGuid();
        var input = CreateInput(kind: AgentDefinitionKind.Orchestrator,
            orchestrationTopologyJson: TopologyJson(triage, [triage, specialist]));
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([StoredRecord(triage), StoredRecord(specialist)]);
        store.UpdateAsync(id, input, Arg.Any<CancellationToken>()).Returns(CreateRecord(input));

        var result = await service.UpdateAsync(id, input).ConfigureAwait(false);

        AssertEx.NotNull(result);
        await store.Received(1).UpdateAsync(id, input, Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task UpdateAsync_WithInvalidOrchestratorTopology_ThrowsBeforeStore()
    {
        var service = CreateService(out var store);
        var triage = Guid.NewGuid();
        var input = CreateInput(kind: AgentDefinitionKind.Orchestrator,
            orchestrationTopologyJson: TopologyJson(triage, [Guid.NewGuid(), Guid.NewGuid()]));

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.UpdateAsync(Guid.NewGuid(), input)).ConfigureAwait(false);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    private static AgentDefinitionService CreateService(out IAgentDefinitionStore store, IReadOnlyList<string>? knownTools = null)
    {
        store = Substitute.For<IAgentDefinitionStore>();
        var offerProvider = Substitute.For<ILocalToolOfferProvider>();
        offerProvider.GetKnownToolNames().Returns(knownTools ?? []);
        return new AgentDefinitionService(store, offerProvider, NullLogger<AgentDefinitionService>.Instance);
    }

    private static AgentDefinitionInput CreateInput(string name = "Agent",
        string? description = null,
        string instructions = "Be helpful.",
        string? modelProfile = "qwen3:8b",
        string? reasoningEffort = null,
        AgentDefinitionKind kind = AgentDefinitionKind.Single,
        IReadOnlyList<string>? allowedTools = null,
        IReadOnlyDictionary<string, bool>? toolApprovals = null,
        string? orchestrationTopologyJson = null)
    {
        return new AgentDefinitionInput(name,
            description,
            instructions,
            modelProfile,
            reasoningEffort,
            kind,
            allowedTools ?? [],
            toolApprovals ?? new Dictionary<string, bool>(),
            orchestrationTopologyJson);
    }

    // Serializes a topology through the SHARED parser's canonical shape so these tests stay coupled to the real wire
    // contract (<c>OrchestrationTopologyJson</c>) rather than a hand-rolled JSON string that could drift from it.
    private static string TopologyJson(Guid triage, IReadOnlyList<Guid> participants, IReadOnlyList<OrchestrationHandoff>? handoffs = null)
    {
        return OrchestrationTopologyJson.Serialize(new OrchestrationTopology
        {
            Version = OrchestrationTopologyJson.CurrentVersion,
            TriageAgentDefinitionId = triage,
            ParticipantAgentDefinitionIds = participants,
            Handoffs = handoffs ?? []
        });
    }

    private static AgentDefinitionRecord StoredRecord(Guid id)
    {
        return new AgentDefinitionRecord(id,
            "Stored",
            Description: null,
            "Be helpful.",
            "qwen3:8b",
            ReasoningEffort: null,
            AgentDefinitionKind.Single,
            [],
            new Dictionary<string, bool>(),
            OrchestrationTopologyJson: null,
            Version: 1,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);
    }

    private static AgentDefinitionRecord CreateRecord(AgentDefinitionInput input)
    {
        return new AgentDefinitionRecord(Guid.NewGuid(),
            input.Name,
            input.Description,
            input.Instructions,
            input.ModelProfile,
            input.ReasoningEffort,
            input.Kind,
            input.AllowedToolNames,
            input.ToolApprovals,
            input.OrchestrationTopologyJson,
            Version: 1,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);
    }
}
