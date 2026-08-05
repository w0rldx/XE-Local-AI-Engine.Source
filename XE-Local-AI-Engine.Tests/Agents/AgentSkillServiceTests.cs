namespace XE_Local_AI_Engine.Tests.Agents;

using Microsoft.Agents.AI;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Stores;
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

        // Leading, trailing AND consecutive dashes are all rejected. The consecutive case is the regression: the
        // superseded ^[a-z0-9]([a-z0-9-]*[a-z0-9])?$ regex accepted "foo--bar", which MAF rejects, so the skill
        // persisted here and then threw ArgumentException when built into an AgentInlineSkill.
        await AssertEx.ThrowsAsync<AgentSkillValidationException>(() =>
            service.CreateAsync(new AgentSkillInput("-leading", "desc", "body"))).ConfigureAwait(false);
        await AssertEx.ThrowsAsync<AgentSkillValidationException>(() =>
            service.CreateAsync(new AgentSkillInput("trailing-", "desc", "body"))).ConfigureAwait(false);
        await AssertEx.ThrowsAsync<AgentSkillValidationException>(() =>
            service.CreateAsync(new AgentSkillInput("foo--bar", "desc", "body"))).ConfigureAwait(false);
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

    // The defect this guards was a silent divergence: our validator and MAF's disagreed, so a name could pass here and
    // then throw at agent-construction time. Asserting "we accept exactly what MAF accepts" — rather than re-stating
    // the rule in a second regex — is the only form of this test that cannot drift back out of sync when the
    // specification moves. Every rejection additionally proves the accompanying AgentInlineSkill construction claim.
    [Test]
    [Arguments("good-name")]
    [Arguments("a")]
    [Arguments("skill1")]
    [Arguments("a-b-c")]
    [Arguments("foo--bar")]
    [Arguments("-leading")]
    [Arguments("trailing-")]
    [Arguments("UPPER")]
    [Arguments("under_score")]
    [Arguments("has space")]
    [Arguments("dot.name")]
    public async Task AgentSkillService_NameVerdict_MatchesMafFrontmatterExactly(string candidate)
    {
        var store = CreateEmptyStore();
        var service = new AgentSkillService(store);

#pragma warning disable MAAI001 // Scoped: the shipped validator is the authority under test.
        var mafAccepts = AgentSkillFrontmatter.ValidateName(candidate, out _);
#pragma warning restore MAAI001

        var serviceAccepted = true;
        try
        {
            await service.CreateAsync(new AgentSkillInput(candidate, "desc", "body")).ConfigureAwait(false);
        }
        catch (AgentSkillValidationException)
        {
            serviceAccepted = false;
        }

        AssertEx.Equal(mafAccepts, serviceAccepted,
            $"The service and MAF must agree on the name '{candidate}'; a disagreement is the D1 defect class.");

        // A name we accept must actually build into a MAF skill — the construction the invocation factory and the
        // sub-agent spawn path both perform. Anything else is the same defect wearing a different hat.
        if (serviceAccepted)
        {
#pragma warning disable MAAI001
            _ = new AgentInlineSkill(candidate, "desc", "body");
#pragma warning restore MAAI001
        }
    }

    // MAF validates only Name and Description, so the four optional frontmatter fields would otherwise reach the store
    // unbounded — and they arrive from imported SKILL.md files, not only from the editor. Each case is the smallest
    // value over its limit.
    [Test]
    public async Task AgentSkillService_Create_RejectsOverLongFrontmatter()
    {
        var store = CreateEmptyStore();
        var service = new AgentSkillService(store);

        await AssertEx.ThrowsAsync<AgentSkillValidationException>(() =>
            service.CreateAsync(new AgentSkillInput("good-name", "desc", "body", License: new string(c: 'a', count: 201)))).ConfigureAwait(false);
        await AssertEx.ThrowsAsync<AgentSkillValidationException>(() =>
            service.CreateAsync(new AgentSkillInput("good-name", "desc", "body", Compatibility: new string(c: 'a', count: 501)))).ConfigureAwait(false);
        await AssertEx.ThrowsAsync<AgentSkillValidationException>(() =>
            service.CreateAsync(new AgentSkillInput("good-name", "desc", "body", AllowedTools: new string(c: 'a', count: 1025)))).ConfigureAwait(false);
        await AssertEx.ThrowsAsync<AgentSkillValidationException>(() =>
            service.CreateAsync(new AgentSkillInput("good-name", "desc", "body",
                Metadata: Enumerable.Range(start: 0, count: 33).ToDictionary(index => $"k{index}", _ => "v")))).ConfigureAwait(false);
        await AssertEx.ThrowsAsync<AgentSkillValidationException>(() =>
            service.CreateAsync(new AgentSkillInput("good-name", "desc", "body",
                Metadata: new Dictionary<string, string>
                {
                    [new string(c: 'k', count: 65)] = "v"
                }))).ConfigureAwait(false);
        await AssertEx.ThrowsAsync<AgentSkillValidationException>(() =>
            service.CreateAsync(new AgentSkillInput("good-name", "desc", "body",
                Metadata: new Dictionary<string, string>
                {
                    ["k"] = new string(c: 'v', count: 513)
                }))).ConfigureAwait(false);

        await store.DidNotReceive().CreateAsync(Arg.Any<AgentSkillInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    // Compatibility is capped at MAF's own MaxCompatibilityLength, so anything we accept must survive construction of
    // the frontmatter MAF builds from it. A cap of ours that exceeded MAF's would persist a value that later throws.
    [Test]
    public void AgentSkillService_CompatibilityCap_DoesNotExceedWhatMafAccepts()
    {
#pragma warning disable MAAI001
        AssertEx.True(AgentSkillFrontmatter.ValidateCompatibility(new string(c: 'a', count: 500), out _),
            "A compatibility value at our cap must still satisfy MAF's own validator.");
        AssertEx.False(AgentSkillFrontmatter.ValidateCompatibility(new string(c: 'a', count: 501), out _),
            "One character past our cap must be where MAF's own limit starts; if this passes, MAF's limit moved and ours should follow.");
#pragma warning restore MAAI001
    }

    [Test]
    public async Task AgentSkillService_Create_RejectsOverLongFields()
    {
        var store = CreateEmptyStore();
        var service = new AgentSkillService(store);

        await AssertEx.ThrowsAsync<AgentSkillValidationException>(() =>
            service.CreateAsync(new AgentSkillInput(new string(c: 'a', count: 65), "desc", "body"))).ConfigureAwait(false);
        await AssertEx.ThrowsAsync<AgentSkillValidationException>(() =>
            service.CreateAsync(new AgentSkillInput("good-name", new string(c: 'a', count: 1025), "body"))).ConfigureAwait(false);
        await AssertEx.ThrowsAsync<AgentSkillValidationException>(() =>
            service.CreateAsync(new AgentSkillInput("good-name", "desc", new string(c: 'a', count: 20001)))).ConfigureAwait(false);

        await store.DidNotReceive().CreateAsync(Arg.Any<AgentSkillInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task AgentSkillService_Create_RejectsCaseInsensitiveDuplicateName()
    {
        var store = Substitute.For<IAgentSkillStore>();
        store.ListAsync(Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<AgentSkillRecord>>([
                 new AgentSkillRecord(Guid.NewGuid(), "kubernetes-debug", "d", "b", Enabled: true, Version: 1, CreatedAtUtc: 10, UpdatedAtUtc: 10)
             ]));
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
        var stored = new AgentSkillRecord(Guid.NewGuid(), input.Name, input.Description, input.Body, Enabled: true, Version: 1, CreatedAtUtc: 10, UpdatedAtUtc: 10);
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
             .Returns(Task.FromResult<IReadOnlyList<AgentSkillRecord>>([new AgentSkillRecord(id, "kubernetes-debug", "d", "b", Enabled: true, Version: 1, CreatedAtUtc: 10, UpdatedAtUtc: 10)]));
        var input = new AgentSkillInput("kubernetes-debug", "Updated description", "## Updated body");
        store.UpdateAsync(id, input, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<AgentSkillRecord?>(new AgentSkillRecord(id, input.Name, input.Description, input.Body, Enabled: true, Version: 2, CreatedAtUtc: 10, UpdatedAtUtc: 20)));
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
