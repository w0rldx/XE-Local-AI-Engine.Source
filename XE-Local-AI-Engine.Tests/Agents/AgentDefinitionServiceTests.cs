namespace XE_Local_AI_Engine.Tests.Agents;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class AgentDefinitionServiceTests
{
    [Test]
    public async Task CreateAsync_WithValidInput_PersistsThroughStore()
    {
        var service = CreateService(out var store, ["GetCurrentTime"]);
        var input = CreateInput(allowedTools: ["GetCurrentTime"]);
        var stored = CreateRecord(input);
        store.AddAsync(input, Arg.Any<CancellationToken>()).Returns(stored);

        var result = await service.CreateAsync(input);

        AssertEx.Equal(stored.Id, result.Id);
        await store.Received(1).AddAsync(input, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_WithEmptyName_ThrowsValidation()
    {
        var service = CreateService(out var store);
        var input = CreateInput("   ");

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.CreateAsync(input));
        await store.DidNotReceive().AddAsync(Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_WithEmptyInstructions_ThrowsValidation()
    {
        var service = CreateService(out var store);
        var input = CreateInput(instructions: "");

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.CreateAsync(input));
        await store.DidNotReceive().AddAsync(Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>());
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

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.CreateAsync(input));
    }

    [Test]
    public async Task CreateAsync_WithInvalidReasoningEffort_ThrowsValidation()
    {
        var service = CreateService(out _);
        var input = CreateInput(reasoningEffort: "turbo");

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.CreateAsync(input));
    }

    // The shared normalizer's whole vocabulary is valid for a saved agent, not a private subset, and only its
    // canonical spelling reaches the store.
    [Test]
    [Arguments(" XHigh ", "xhigh")]
    [Arguments("MINIMAL", "minimal")]
    [Arguments("On", "on")]
    public async Task CreateAsync_WithNormalizerReasoningEffort_PersistsCanonicalValue(string requested, string expected)
    {
        var service = CreateService(out var store);
        AgentDefinitionInput? persisted = null;
        store.AddAsync(Arg.Do<AgentDefinitionInput>(input => persisted = input), Arg.Any<CancellationToken>())
             .Returns(callInfo => CreateRecord(callInfo.Arg<AgentDefinitionInput>()));

        var result = await service.CreateAsync(CreateInput(reasoningEffort: requested));

        AssertEx.Equal(expected, AssertEx.NotNull(persisted).ReasoningEffort);
        AssertEx.Equal(expected, result.ReasoningEffort);
    }

    [Test]
    public async Task UpdateAsync_WithXHighReasoningEffort_PersistsCanonicalValue()
    {
        var service = CreateService(out var store);
        var id = Guid.NewGuid();
        AgentDefinitionInput? persisted = null;
        store.UpdateAsync(id, Arg.Do<AgentDefinitionInput>(input => persisted = input), Arg.Any<CancellationToken>())
             .Returns(callInfo => CreateRecord(callInfo.Arg<AgentDefinitionInput>()));

        _ = await service.UpdateAsync(id, CreateInput(reasoningEffort: "XHIGH"));

        AssertEx.Equal("xhigh", AssertEx.NotNull(persisted).ReasoningEffort);
    }

    [Test]
    public async Task CreateAsync_WithUnknownToolName_DoesNotThrow_AndPersists()
    {
        // An unknown tool name is a warning, not a failure — a tool can be reinstalled later. Persistence proceeds.
        var service = CreateService(out var store, ["GetCurrentTime"]);
        var input = CreateInput(allowedTools: ["GetCurrentTime", "MaybeLaterTool"]);
        store.AddAsync(input, Arg.Any<CancellationToken>()).Returns(CreateRecord(input));

        var result = await service.CreateAsync(input);

        AssertEx.NotNull(result);
        await store.Received(1).AddAsync(input, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateAsync_WhenStoreReturnsNull_ReturnsNull()
    {
        var service = CreateService(out var store, ["GetCurrentTime"]);
        var id = Guid.NewGuid();
        var input = CreateInput(allowedTools: ["GetCurrentTime"]);
        store.UpdateAsync(id, input, Arg.Any<CancellationToken>()).Returns((AgentDefinitionRecord?)null);

        var result = await service.UpdateAsync(id, input);

        AssertEx.True(result is null, "Updating a missing definition must return null.");
    }

    [Test]
    public async Task UpdateAsync_WithInvalidInput_ThrowsBeforeStore()
    {
        var service = CreateService(out var store);
        var input = CreateInput("");

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.UpdateAsync(Guid.NewGuid(), input));
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>());
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

        AssertEx.Equal(expected: true, await service.DeleteAsync(id));
        AssertEx.Equal(record.Id, (await service.GetByIdAsync(id))!.Id);
        AssertEx.Equal(expected: 1, (await service.ListAsync()).Count);
    }

    [Test]
    public async Task GetByKeyAsync_WithId_UsesDirectLookup()
    {
        var service = CreateService(out var store);
        var record = CreateRecord(CreateInput());
        store.GetByIdAsync(record.Id, Arg.Any<CancellationToken>()).Returns(record);

        var result = await service.GetByKeyAsync(record.Id.ToString());

        AssertEx.Equal(record.Id, AssertEx.NotNull(result).Id);
        await store.DidNotReceive().ListAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetByKeyAsync_WithName_UsesExactOrdinalMatch()
    {
        var service = CreateService(out var store);
        var record = CreateRecord(CreateInput("Exact Name"));
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([record]);

        AssertEx.Equal(record.Id, AssertEx.NotNull(await service.GetByKeyAsync("Exact Name")).Id);
        AssertEx.True(await service.GetByKeyAsync("exact name") is null,
            "agent names must resolve by exact ordinal match.");
    }

    // Q5 / I-D6: names are not unique, and the first match used to win silently.
    [Test]
    public async Task GetByKeyAsync_WithSharedName_ThrowsAmbiguousName()
    {
        var service = CreateService(out var store);
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([CreateRecord(CreateInput("Twin")), CreateRecord(CreateInput("Twin"))]);

        _ = await Assert.ThrowsAsync<AgentDefinitionAmbiguousNameException>(() => service.GetByKeyAsync("Twin"));
    }

    [Test]
    public async Task GetByKeyAsync_WithBlankKey_ReturnsNullWithoutStoreRead()
    {
        var service = CreateService(out var store);

        AssertEx.True(await service.GetByKeyAsync("   ") is null,
            "blank agent keys must not resolve.");
        await store.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().ListAsync(Arg.Any<CancellationToken>());
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

        var result = await service.CreateAsync(input);

        AssertEx.NotNull(result);
        await store.Received(1).AddAsync(input, Arg.Any<CancellationToken>());
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

        var result = await service.CreateAsync(input);

        AssertEx.NotNull(result);
        await store.Received(1).AddAsync(input, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_OrchestratorWithoutTopology_ThrowsValidation()
    {
        var service = CreateService(out var store);
        var input = CreateInput(kind: AgentDefinitionKind.Orchestrator, orchestrationTopologyJson: null);

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.CreateAsync(input));
        await store.DidNotReceive().AddAsync(Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_OrchestratorWithMalformedTopologyJson_ThrowsValidation()
    {
        var service = CreateService(out var store);
        var input = CreateInput(kind: AgentDefinitionKind.Orchestrator, orchestrationTopologyJson: "{ not valid json ");

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.CreateAsync(input));
        await store.DidNotReceive().AddAsync(Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>());
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

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.CreateAsync(input));
        await store.DidNotReceive().AddAsync(Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_OrchestratorWithFewerThanTwoParticipants_ThrowsValidation()
    {
        var service = CreateService(out var store);
        var triage = Guid.NewGuid();
        var input = CreateInput(kind: AgentDefinitionKind.Orchestrator,
            orchestrationTopologyJson: TopologyJson(triage, [triage]));

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.CreateAsync(input));
        await store.DidNotReceive().AddAsync(Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_OrchestratorTriageNotInParticipants_ThrowsValidation()
    {
        var service = CreateService(out var store);
        var triage = Guid.NewGuid();
        var input = CreateInput(kind: AgentDefinitionKind.Orchestrator,
            orchestrationTopologyJson: TopologyJson(triage, [Guid.NewGuid(), Guid.NewGuid()]));

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.CreateAsync(input));
        await store.DidNotReceive().AddAsync(Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>());
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

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.CreateAsync(input));
        await store.DidNotReceive().AddAsync(Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>());
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

        var result = await service.CreateAsync(input);

        AssertEx.NotNull(result);
        await store.Received(1).AddAsync(input, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_SingleAgentWithTopologyPayload_ThrowsValidation()
    {
        // A single agent must not carry a topology; a stray payload would be silently ignored at runtime, so reject it.
        var service = CreateService(out var store);
        var input = CreateInput(kind: AgentDefinitionKind.Single,
            orchestrationTopologyJson: TopologyJson(Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()]));

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.CreateAsync(input));
        await store.DidNotReceive().AddAsync(Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>());
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

        var result = await service.UpdateAsync(id, input);

        AssertEx.NotNull(result);
        await store.Received(1).UpdateAsync(id, input, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateAsync_WithInvalidOrchestratorTopology_ThrowsBeforeStore()
    {
        var service = CreateService(out var store);
        var triage = Guid.NewGuid();
        var input = CreateInput(kind: AgentDefinitionKind.Orchestrator,
            orchestrationTopologyJson: TopologyJson(triage, [Guid.NewGuid(), Guid.NewGuid()]));

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.UpdateAsync(Guid.NewGuid(), input));
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateAsync_DefaultAssistant_PersistsEmptyToolListAndApprovals()
    {
        var service = CreateService(out var store, ["list_files"]);
        var id = Guid.NewGuid();
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(StoredRecord(id) with
        {
            Source = AgentDefinitionSource.Seeded,
            SeedSlug = AgentDefaults.DefaultAgentSeedSlug
        });
        var input = CreateInput(allowedTools: ["list_files"],
            toolApprovals: new Dictionary<string, bool>
            {
                ["list_files"] = true
            });

        await service.UpdateAsync(id, input);

        await store.Received(1).UpdateAsync(id,
            Arg.Is<AgentDefinitionInput>(stored => stored.AllowedToolNames.Count == 0 && stored.ToolApprovals.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateAsync_OrdinaryAgent_KeepsItsToolList()
    {
        var service = CreateService(out var store, ["list_files"]);
        var id = Guid.NewGuid();
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(StoredRecord(id));
        var input = CreateInput(allowedTools: ["list_files"]);

        await service.UpdateAsync(id, input);

        await store.Received(1).UpdateAsync(id, input, Arg.Any<CancellationToken>());
    }

    private static AgentDefinitionService CreateService(out IAgentDefinitionStore store, IReadOnlyList<string>? knownTools = null)
    {
        store = Substitute.For<IAgentDefinitionStore>();
        var offerProvider = Substitute.For<ILocalToolOfferProvider>();
        offerProvider.GetKnownToolNamesAsync(Arg.Any<CancellationToken>()).Returns(knownTools ?? []);
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
        return new AgentDefinitionInput
        {
            Name = name,
            Description = description,
            Instructions = instructions,
            ModelProfile = modelProfile,
            ReasoningEffort = reasoningEffort,
            Kind = kind,
            AllowedToolNames = allowedTools ?? [],
            ToolApprovals = toolApprovals ?? new Dictionary<string, bool>(),
            OrchestrationTopologyJson = orchestrationTopologyJson
        };
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
        return new AgentDefinitionRecord
        {
            Id = id,
            Name = "Stored",
            Description = null,
            Instructions = "Be helpful.",
            ModelProfile = "qwen3:8b",
            ReasoningEffort = null,
            Kind = AgentDefinitionKind.Single,
            AllowedToolNames = [],
            ToolApprovals = new Dictionary<string, bool>(),
            OrchestrationTopologyJson = null,
            Version = 1,
            CreatedAtUtc = 10,
            UpdatedAtUtc = 10
        };
    }

    private static AgentDefinitionRecord CreateRecord(AgentDefinitionInput input)
    {
        return new AgentDefinitionRecord
        {
            Id = Guid.NewGuid(),
            Name = input.Name,
            Description = input.Description,
            Instructions = input.Instructions,
            ModelProfile = input.ModelProfile,
            ReasoningEffort = input.ReasoningEffort,
            Kind = input.Kind,
            AllowedToolNames = input.AllowedToolNames,
            ToolApprovals = input.ToolApprovals,
            OrchestrationTopologyJson = input.OrchestrationTopologyJson,
            Version = 1,
            CreatedAtUtc = 10,
            UpdatedAtUtc = 10
        };
    }
}
