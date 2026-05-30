namespace XE_Local_AI_Engine.Tests.Agents;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class AgentDefinitionServiceTests
{
    [Test]
    public async Task CreateAsync_WithValidInput_PersistsThroughStore()
    {
        var service = CreateService(out var store, knownTools: ["GetCurrentTime"]);
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
        var input = CreateInput(name: "   ");

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
        var service = CreateService(out _, knownTools: ["GetCurrentTime"]);
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
        var service = CreateService(out var store, knownTools: ["GetCurrentTime"]);
        var input = CreateInput(allowedTools: ["GetCurrentTime", "MaybeLaterTool"]);
        store.AddAsync(input, Arg.Any<CancellationToken>()).Returns(CreateRecord(input));

        var result = await service.CreateAsync(input).ConfigureAwait(false);

        AssertEx.NotNull(result);
        await store.Received(1).AddAsync(input, Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task UpdateAsync_WhenStoreReturnsNull_ReturnsNull()
    {
        var service = CreateService(out var store, knownTools: ["GetCurrentTime"]);
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
        var input = CreateInput(name: "");

        await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.UpdateAsync(Guid.NewGuid(), input)).ConfigureAwait(false);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task DeleteGetList_DelegateToStore()
    {
        var service = CreateService(out var store, knownTools: ["GetCurrentTime"]);
        var id = Guid.NewGuid();
        var record = CreateRecord(CreateInput(allowedTools: ["GetCurrentTime"]));
        store.DeleteAsync(id, Arg.Any<CancellationToken>()).Returns(true);
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(record);
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([record]);

        AssertEx.Equal(true, await service.DeleteAsync(id).ConfigureAwait(false));
        AssertEx.Equal(record.Id, (await service.GetByIdAsync(id).ConfigureAwait(false))!.Id);
        AssertEx.Equal(1, (await service.ListAsync().ConfigureAwait(false)).Count);
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
        IReadOnlyDictionary<string, bool>? toolApprovals = null)
    {
        return new AgentDefinitionInput(name,
            description,
            instructions,
            modelProfile,
            reasoningEffort,
            kind,
            allowedTools ?? [],
            toolApprovals ?? new Dictionary<string, bool>(),
            null);
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
            1,
            10,
            10);
    }
}
