namespace XE_Local_AI_Engine.Tests.Benchmarks;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class BenchmarkFreezeDependencyServiceTests
{
    [Test]
    public async Task Capture_ChangesWhenAssignedSkillResourceChanges()
    {
        var agentId = Guid.NewGuid();
        var skillId = Guid.NewGuid();
        var definitions = Substitute.For<IAgentDefinitionStore>();
        definitions.GetByIdAsync(agentId, Arg.Any<CancellationToken>()).Returns(Definition(agentId, skillId));
        var playbooks = Substitute.For<IPlaybookActionStore>();
        playbooks.ListByAgentAsync(agentId, Arg.Any<CancellationToken>()).Returns([]);
        var skills = Substitute.For<IAgentSkillStore>();
        skills.GetByIdAsync(skillId, Arg.Any<CancellationToken>()).Returns(Skill(skillId));
        var resourceContent = "first";
        skills.ListResourcesAsync(skillId, Arg.Any<CancellationToken>()).Returns(_ =>
            new[]
            {
                new AgentSkillResourceRecord(Guid.Parse("11111111-1111-1111-1111-111111111111"), skillId, "notes.md", "notes", "text/markdown", resourceContent, resourceContent.Length)
            });
        var customTools = Substitute.For<ICustomToolStore>();
        customTools.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        var profiles = Substitute.For<IInferenceProfileStore>();
        profiles.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        var service = new BenchmarkFreezeDependencyService(definitions, playbooks, skills, customTools, profiles);
        var runtime = new ResolvedAgentRuntime("prompt", [], null, null, 4, agentId, "Agent", Kind: AgentDefinitionKind.Single);

        var first = await service.CaptureAsync(agentId, runtime, "primary.gguf", null, CancellationToken.None);
        resourceContent = "second";
        var second = await service.CaptureAsync(agentId, runtime, "primary.gguf", null, CancellationToken.None);

        AssertEx.NotEqual(first.SkillAssignmentSetHash, second.SkillAssignmentSetHash);
        AssertEx.Equal(first.AgentDependencyHash, second.AgentDependencyHash);
    }

    private static AgentDefinitionRecord Definition(Guid id, Guid skillId) =>
        new(id, "Agent", null, "instructions", null, null, AgentDefinitionKind.Single, [], new Dictionary<string, bool>(), null,
            4, 1, 1, AllowedSkillIds: [skillId]);

    private static AgentSkillRecord Skill(Guid id) =>
        new(id, "skill", "description", "body", true, 2, 1, 1);
}
