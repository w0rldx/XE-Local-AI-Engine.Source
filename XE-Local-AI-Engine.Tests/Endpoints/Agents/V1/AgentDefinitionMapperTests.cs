namespace XE_Local_AI_Engine.Tests.Endpoints.Agents.V1;

using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The wire's <c>IsDefaultAssistant</c> flag follows the resolver's provenance rule (seeded row + the default seed
///     slug), so the client never has to guess from the display name.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class AgentDefinitionMapperTests
{
    private static AgentDefinitionRecord Definition(AgentDefinitionSource source, string? seedSlug)
    {
        return new AgentDefinitionRecord
        {
            Id = Guid.NewGuid(),
            Name = AgentDefaults.DefaultAgentName,
            Description = null,
            Instructions = "Be helpful.",
            ModelProfile = null,
            ReasoningEffort = null,
            Kind = AgentDefinitionKind.Single,
            AllowedToolNames = [],
            ToolApprovals = new Dictionary<string, bool>(StringComparer.Ordinal),
            OrchestrationTopologyJson = null,
            Version = 1,
            CreatedAtUtc = 0,
            UpdatedAtUtc = 0,
            Source = source,
            SeedSlug = seedSlug
        };
    }

    [Test]
    public void ToResponse_WhenSeededDefaultAssistant_FlagsIt()
    {
        var response = Definition(AgentDefinitionSource.Seeded, AgentDefaults.DefaultAgentSeedSlug).ToResponse();

        AssertEx.True(response.IsDefaultAssistant);
    }

    [Test]
    public void ToResponse_WhenOnlyTheNameMatches_DoesNotFlagIt()
    {
        // An operator row sharing the seeded name, and a different seeded persona, are both not the Default Assistant.
        AssertEx.False(Definition(AgentDefinitionSource.Manual, seedSlug: null).ToResponse().IsDefaultAssistant);
        AssertEx.False(Definition(AgentDefinitionSource.Seeded, AgentDefaults.CoderAgentSeedSlug).ToResponse().IsDefaultAssistant);
    }
}
