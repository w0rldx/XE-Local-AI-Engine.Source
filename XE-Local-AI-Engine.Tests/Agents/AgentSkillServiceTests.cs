namespace XE_Local_AI_Engine.Tests.Agents;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class AgentSkillServiceTests
{
    [Test]
    public async Task AgentSkillService_Create_RejectsBadNameAndBlankBody()
    {
        var store = CreateEmptyStore();
        var service = new AgentSkillService(store);

        // Uppercase + space is not a MAF-safe kebab-case skill name.
        await AssertEx.ThrowsAsync<AgentSkillValidationException>(() =>
            service.CreateAsync(new AgentSkillInput("Bad Name", "desc", "body"))).ConfigureAwait(false);

        // Leading and trailing dashes are rejected by the name regex (a doubled internal dash is permitted by the
        // ^[a-z0-9]([a-z0-9-]*[a-z0-9])?$ shape — only the edge anchors forbid leading/trailing dashes).
        await AssertEx.ThrowsAsync<AgentSkillValidationException>(() =>
            service.CreateAsync(new AgentSkillInput("-leading", "desc", "body"))).ConfigureAwait(false);
        await AssertEx.ThrowsAsync<AgentSkillValidationException>(() =>
            service.CreateAsync(new AgentSkillInput("trailing-", "desc", "body"))).ConfigureAwait(false);
        await AssertEx.ThrowsAsync<AgentSkillValidationException>(() =>
            service.CreateAsync(new AgentSkillInput("UPPER", "desc", "body"))).ConfigureAwait(false);

        // Blank body is rejected.
        await AssertEx.ThrowsAsync<AgentSkillValidationException>(() =>
            service.CreateAsync(new AgentSkillInput("good-name", "desc", "   "))).ConfigureAwait(false);

        // Blank description is rejected.
        await AssertEx.ThrowsAsync<AgentSkillValidationException>(() =>
            service.CreateAsync(new AgentSkillInput("good-name", "  ", "body"))).ConfigureAwait(false);

        // None of the rejected inputs reached the store.
        await store.DidNotReceive().CreateAsync(Arg.Any<AgentSkillInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task AgentSkillService_Create_RejectsOverLongFields()
    {
        var store = CreateEmptyStore();
        var service = new AgentSkillService(store);

        await AssertEx.ThrowsAsync<AgentSkillValidationException>(() =>
            service.CreateAsync(new AgentSkillInput(new string('a', 65), "desc", "body"))).ConfigureAwait(false);
        await AssertEx.ThrowsAsync<AgentSkillValidationException>(() =>
            service.CreateAsync(new AgentSkillInput("good-name", new string('a', 1025), "body"))).ConfigureAwait(false);
        await AssertEx.ThrowsAsync<AgentSkillValidationException>(() =>
            service.CreateAsync(new AgentSkillInput("good-name", "desc", new string('a', 20001)))).ConfigureAwait(false);

        await store.DidNotReceive().CreateAsync(Arg.Any<AgentSkillInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task AgentSkillService_Create_RejectsCaseInsensitiveDuplicateName()
    {
        var store = Substitute.For<IAgentSkillStore>();
        store.ListAsync(Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<AgentSkillRecord>>(
                 [new AgentSkillRecord(Guid.NewGuid(), "kubernetes-debug", "d", "b", true, 1, 10, 10)]));
        var service = new AgentSkillService(store);

        // Same name, different casing — NOCASE uniqueness must reject it.
        await AssertEx.ThrowsAsync<AgentSkillValidationException>(() =>
            service.CreateAsync(new AgentSkillInput("KUBERNETES-DEBUG", "desc", "body"))).ConfigureAwait(false);

        await store.DidNotReceive().CreateAsync(Arg.Any<AgentSkillInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task AgentSkillService_Create_PersistsValidSkill()
    {
        var store = CreateEmptyStore();
        var input = new AgentSkillInput("kubernetes-debug", "Debug k8s", "## Body");
        var stored = new AgentSkillRecord(Guid.NewGuid(), input.Name, input.Description, input.Body, true, 1, 10, 10);
        store.CreateAsync(input, Arg.Any<CancellationToken>()).Returns(Task.FromResult(stored));
        var service = new AgentSkillService(store);

        var result = await service.CreateAsync(input).ConfigureAwait(false);

        AssertEx.Equal(stored.Id, result.Id);
        await store.Received(1).CreateAsync(input, Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task AgentSkillService_Update_AllowsSameNameForSameSkill()
    {
        var id = Guid.NewGuid();
        var store = Substitute.For<IAgentSkillStore>();
        store.ListAsync(Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<AgentSkillRecord>>(
                 [new AgentSkillRecord(id, "kubernetes-debug", "d", "b", true, 1, 10, 10)]));
        var input = new AgentSkillInput("kubernetes-debug", "Updated description", "## Updated body");
        store.UpdateAsync(id, input, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<AgentSkillRecord?>(new AgentSkillRecord(id, input.Name, input.Description, input.Body, true, 2, 10, 20)));
        var service = new AgentSkillService(store);

        // Re-saving the same skill with its own (unchanged) name must NOT trip the NOCASE-uniqueness guard.
        var result = await service.UpdateAsync(id, input).ConfigureAwait(false);

        AssertEx.NotNull(result);
        await store.Received(1).UpdateAsync(id, input, Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    private static IAgentSkillStore CreateEmptyStore()
    {
        var store = Substitute.For<IAgentSkillStore>();
        store.ListAsync(Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<AgentSkillRecord>>([]));
        return store;
    }
}
