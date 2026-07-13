namespace XE_Local_AI_Engine.Tests.Agents;

using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class OrchestrationTopologyTests
{
    [Test]
    public void TryParse_WhenNull_ReturnsNull()
    {
        AssertEx.True(OrchestrationTopologyJson.TryParse(null) is null, "A null payload must parse to null (no topology).");
    }

    [Test]
    public void TryParse_WhenBlank_ReturnsNull()
    {
        AssertEx.True(OrchestrationTopologyJson.TryParse("   ") is null, "A blank payload must parse to null (no topology).");
    }

    [Test]
    public void TryParse_WhenMalformedJson_ReturnsNull()
    {
        AssertEx.True(OrchestrationTopologyJson.TryParse("{ not json") is null, "Malformed JSON must parse to null rather than throw.");
    }

    [Test]
    public void TryParse_WhenUnknownVersion_ReturnsNull()
    {
        var topology = OrchestrationTopologyJson.TryParse("{\"version\":2,\"triageAgentDefinitionId\":\"" + Guid.NewGuid().ToString("D") + "\"}");

        AssertEx.True(topology is null, "A version this build does not understand must parse to null (forward-compat degrade).");
    }

    [Test]
    public void TryParse_WhenParticipantsExceedCap_ReturnsNull()
    {
        // Fail-closed cap: an oversized participant list would fan out into one DB lookup per id per turn.
        var triage = Guid.NewGuid();
        var participants = new List<Guid>
        {
            triage
        };
        for (var i = 0; i < OrchestrationTopologyJson.MaxParticipants; i++)
        {
            participants.Add(Guid.NewGuid());
        }

        var topology = new OrchestrationTopology
        {
            Version = 1,
            TriageAgentDefinitionId = triage,
            ParticipantAgentDefinitionIds = participants
        };

        AssertEx.True(OrchestrationTopologyJson.TryParse(OrchestrationTopologyJson.Serialize(topology)) is null,
            "A participant list over the cap must parse to null (fail-closed).");
    }

    [Test]
    public void TryParse_WhenMaxTurnsPerAgentExceedsCeiling_ReturnsNull()
    {
        // Fail-closed cap (MED-004): an over-ceiling per-agent turn count fans an agent out into an arbitrarily long
        // autonomous loop per turn, so any API path (including direct CRUD) is rejected server-side.
        var triage = Guid.NewGuid();
        var specialist = Guid.NewGuid();
        var topology = new OrchestrationTopology
        {
            Version = 1,
            TriageAgentDefinitionId = triage,
            ParticipantAgentDefinitionIds = [triage, specialist],
            MaxTurnsPerAgent = OrchestrationTopologyJson.MaxTurnsPerAgentCeiling + 1
        };

        AssertEx.True(OrchestrationTopologyJson.TryParse(OrchestrationTopologyJson.Serialize(topology)) is null,
            "A per-agent turn cap over the ceiling must parse to null (fail-closed).");
    }

    [Test]
    public void TryParse_WhenMaxTurnsPerAgentAtCeiling_Parses()
    {
        var triage = Guid.NewGuid();
        var specialist = Guid.NewGuid();
        var topology = new OrchestrationTopology
        {
            Version = 1,
            TriageAgentDefinitionId = triage,
            ParticipantAgentDefinitionIds = [triage, specialist],
            MaxTurnsPerAgent = OrchestrationTopologyJson.MaxTurnsPerAgentCeiling
        };

        var parsed = OrchestrationTopologyJson.TryParse(OrchestrationTopologyJson.Serialize(topology));

        AssertEx.NotNull(parsed);
        AssertEx.Equal(OrchestrationTopologyJson.MaxTurnsPerAgentCeiling, parsed!.MaxTurnsPerAgent);
    }

    [Test]
    public void TryParse_WhenHandoffsExceedCap_ReturnsNull()
    {
        var triage = Guid.NewGuid();
        var specialist = Guid.NewGuid();
        var handoffs = new List<OrchestrationHandoff>();
        for (var i = 0; i <= OrchestrationTopologyJson.MaxHandoffs; i++)
        {
            handoffs.Add(new OrchestrationHandoff
            {
                FromAgentDefinitionId = triage,
                ToAgentDefinitionId = specialist
            });
        }

        var topology = new OrchestrationTopology
        {
            Version = 1,
            TriageAgentDefinitionId = triage,
            ParticipantAgentDefinitionIds = [triage, specialist],
            Handoffs = handoffs
        };

        AssertEx.True(OrchestrationTopologyJson.TryParse(OrchestrationTopologyJson.Serialize(topology)) is null,
            "A handoff list over the cap must parse to null (fail-closed).");
    }

    [Test]
    public void TryParse_WhenParticipantsAtCap_StillParses()
    {
        // Exactly at the cap is allowed — only a list STRICTLY over the cap fails closed.
        var triage = Guid.NewGuid();
        var participants = new List<Guid>
        {
            triage
        };
        while (participants.Count < OrchestrationTopologyJson.MaxParticipants)
        {
            participants.Add(Guid.NewGuid());
        }

        var topology = new OrchestrationTopology
        {
            Version = 1,
            TriageAgentDefinitionId = triage,
            ParticipantAgentDefinitionIds = participants
        };

        var parsed = OrchestrationTopologyJson.TryParse(OrchestrationTopologyJson.Serialize(topology));

        AssertEx.NotNull(parsed);
        AssertEx.Equal(OrchestrationTopologyJson.MaxParticipants, parsed!.ParticipantAgentDefinitionIds.Count);
    }

    [Test]
    public void TryParse_WhenValid_ReadsAllFields()
    {
        var triage = Guid.NewGuid();
        var specialist = Guid.NewGuid();
        var json = "{" +
                   "\"version\":1," +
                   "\"triageAgentDefinitionId\":\"" + triage.ToString("D") + "\"," +
                   "\"participantAgentDefinitionIds\":[\"" + triage.ToString("D") + "\",\"" + specialist.ToString("D") + "\"]," +
                   "\"handoffs\":[{\"fromAgentDefinitionId\":\"" + triage.ToString("D") + "\",\"toAgentDefinitionId\":\"" + specialist.ToString("D") + "\",\"reason\":\"route here\"}]," +
                   "\"maxTurnsPerAgent\":5," +
                   "\"returnToPrevious\":true}";

        var topology = OrchestrationTopologyJson.TryParse(json);

        AssertEx.NotNull(topology);
        AssertEx.Equal(expected: 1, topology!.Version);
        AssertEx.Equal(triage, topology.TriageAgentDefinitionId);
        AssertEx.Equal(expected: 2, topology.ParticipantAgentDefinitionIds.Count);
        AssertEx.Contains(topology.ParticipantAgentDefinitionIds, id => id == specialist);
        AssertEx.Equal(expected: 1, topology.Handoffs.Count);
        AssertEx.Equal(triage, topology.Handoffs[0].FromAgentDefinitionId);
        AssertEx.Equal(specialist, topology.Handoffs[0].ToAgentDefinitionId);
        AssertEx.Equal("route here", topology.Handoffs[0].Reason);
        AssertEx.Equal(expected: 5, topology.MaxTurnsPerAgent);
        AssertEx.True(topology.ReturnToPrevious, "returnToPrevious must round-trip.");
    }

    [Test]
    public void SerializeThenParse_RoundTrips()
    {
        var triage = Guid.NewGuid();
        var specialist = Guid.NewGuid();
        var topology = new OrchestrationTopology
        {
            Version = 1,
            TriageAgentDefinitionId = triage,
            ParticipantAgentDefinitionIds = [triage, specialist],
            Handoffs =
            [
                new OrchestrationHandoff
                {
                    FromAgentDefinitionId = triage,
                    ToAgentDefinitionId = specialist,
                    Reason = "specialist work"
                }
            ],
            MaxTurnsPerAgent = 8,
            ReturnToPrevious = false
        };

        var roundTripped = OrchestrationTopologyJson.TryParse(OrchestrationTopologyJson.Serialize(topology));

        AssertEx.NotNull(roundTripped);
        AssertEx.Equal(topology.TriageAgentDefinitionId, roundTripped!.TriageAgentDefinitionId);
        AssertEx.Equal(topology.ParticipantAgentDefinitionIds.Count, roundTripped.ParticipantAgentDefinitionIds.Count);
        AssertEx.Equal(topology.Handoffs.Count, roundTripped.Handoffs.Count);
        AssertEx.Equal("specialist work", roundTripped.Handoffs[0].Reason);
        AssertEx.Equal(expected: 8, roundTripped.MaxTurnsPerAgent);
    }

    [Test]
    public void TryParse_WhenEmptyHandoffs_ReturnsEmptyList()
    {
        var triage = Guid.NewGuid();
        var json = "{\"version\":1,\"triageAgentDefinitionId\":\"" + triage.ToString("D") + "\",\"participantAgentDefinitionIds\":[\"" + triage.ToString("D") + "\"]}";

        var topology = OrchestrationTopologyJson.TryParse(json);

        AssertEx.NotNull(topology);
        AssertEx.Empty(topology!.Handoffs);
    }
}
