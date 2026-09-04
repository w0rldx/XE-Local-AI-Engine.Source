namespace XE_Local_AI_Engine.Tests.Chat;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The withdrawal half of <see cref="AskUserToolOffer" /> — the union half is pinned through the resolvers that
///     call it. Both halves live in one type so they cannot drift, and these tests hold the pure list/spec rewrites; the
///     seam that decides WHEN to withdraw is pinned in <c>NodeChatStreamServiceTests</c> and <c>WorkSessionStepLoopTests</c>.
/// </summary>
public sealed class AskUserToolOfferTests
{
    [Test]
    public void Withdraw_RemovesOnlyAskUser()
    {
        var withdrawn = AssertEx.NotNull(AskUserToolOffer.Withdraw([Tool("read_file"), Tool(AskUserTool.ToolName), Tool("record_finding")]));

        AssertEx.Equal(expected: 2, withdrawn.Count);
        AssertEx.Contains(withdrawn, tool => tool.Name == "read_file");
        AssertEx.Contains(withdrawn, tool => tool.Name == "record_finding");
        AssertEx.False(withdrawn.Any(tool => tool.Name == AskUserTool.ToolName));
    }

    [Test]
    public void Withdraw_WhenTheToolWasNeverOffered_ReturnsTheSameListUntouched()
    {
        // The ordinary path has to stay allocation-free: every send of a workflow-owned session runs this.
        IReadOnlyList<AllowedToolDto> offered = [Tool("read_file")];

        AssertEx.True(ReferenceEquals(offered, AskUserToolOffer.Withdraw(offered)));
        AssertEx.Null(AskUserToolOffer.Withdraw((IReadOnlyList<AllowedToolDto>?)null), "A turn that offers no tools has nothing to withdraw.");
    }

    [Test]
    public void Withdraw_FiltersEveryOrchestrationParticipant()
    {
        // Each participant carries its own projected list, so a workflow node bound to an Orchestrator agent needs all
        // of them filtered — not just the send's single allowed-tool list.
        var spec = Spec([Tool("read_file"), Tool(AskUserTool.ToolName)], [Tool(AskUserTool.ToolName)]);

        var withdrawn = AssertEx.NotNull(AskUserToolOffer.Withdraw(spec));

        AssertEx.False(withdrawn.Participants.Any(participant => participant.Tools.Any(tool => tool.Name == AskUserTool.ToolName)));
        AssertEx.Equal(expected: 1, withdrawn.Participants[0].Tools.Count);
        AssertEx.Empty(withdrawn.Participants[1].Tools);
        AssertEx.Equal("triage", withdrawn.TriageParticipantKey, "The rewrite touches the participants' tools and nothing else.");
    }

    [Test]
    public void Withdraw_WhenNoParticipantWasOfferedTheTool_ReturnsTheSameSpec()
    {
        var spec = Spec([Tool("read_file")], [Tool("record_finding")]);

        AssertEx.True(ReferenceEquals(spec, AskUserToolOffer.Withdraw(spec)));
        AssertEx.Null(AskUserToolOffer.Withdraw((OrchestrationSpec?)null), "A single-agent turn carries no spec.");
    }

    private static AllowedToolDto Tool(string name) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Location = ToolLocation.ClientLocal,
            ParameterSchema = "{\"type\":\"object\"}",
            RequiresApproval = false,
            Category = ToolCategory.ReadLocal
        };

    private static OrchestrationSpec Spec(IReadOnlyList<AllowedToolDto> triageTools, IReadOnlyList<AllowedToolDto> specialistTools) =>
        new()
        {
            TriageParticipantKey = "triage",
            Participants =
            [
                new OrchestrationSpecParticipant
                {
                    Key = "triage",
                    Name = "Triage",
                    Instructions = "Route the work.",
                    Tools = triageTools
                },
                new OrchestrationSpecParticipant
                {
                    Key = "specialist",
                    Name = "Specialist",
                    Instructions = "Do the work.",
                    Tools = specialistTools
                }
            ],
            Edges = [],
            MaxTurnsPerAgent = 4,
            ReturnToPrevious = false
        };
}
