namespace XE_Local_AI_Engine.Tests.Eval;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Eval;
using XE_Local_AI_Engine.Client.Services.Eval.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Golden harvest orchestration unit tests (deterministic, no model/DB). The harvest service scans thumbs-up sources
///     via a faked <see cref="IGoldenHarvestSourceStore" />, dedups against already-harvested source ids, builds the
///     seeded rubric + camelCase input turns, and stages each fresh candidate through
///     <see cref="IGoldenConversationService.CreateHarvestedAsync" />. Counts split across created / duplicate / skipped.
/// </summary>
public sealed class GoldenHarvestServiceTests
{
    private const string RubricSeedPrefix = "The response should be consistent with this operator-approved answer:";
    private static readonly Guid AgentId = Guid.NewGuid();

    [Test]
    public async Task HarvestAsync_WhenAgentUnknown_ReportsAgentMissingWithZeroCounts()
    {
        var harness = new Harness();
        harness.AgentStore.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<AgentDefinitionRecord?>(null));

        var outcome = await harness.Service.HarvestAsync(AgentId).ConfigureAwait(false);

        AssertEx.False(outcome.AgentExists, "An unknown agent should report AgentExists=false.");
        AssertEx.Equal(expected: 0, outcome.ThumbsUpScanned);
        AssertEx.Equal(expected: 0, outcome.CreatedCount);
        AssertEx.Equal(expected: 0, outcome.DuplicateCount);
        AssertEx.Equal(expected: 0, outcome.SkippedCount);
        await harness.ConversationService.DidNotReceive()
                     .CreateHarvestedAsync(Arg.Any<GoldenConversationCreateInput>(), Arg.Any<CancellationToken>())
                     .ConfigureAwait(false);
    }

    [Test]
    public async Task HarvestAsync_WithOneFreshSource_CreatesOneHarvestedCandidateWithSeededRubricAndTurns()
    {
        var harness = new Harness();
        var sourceMessageId = Guid.NewGuid();
        var sourceConversationId = Guid.NewGuid();
        var source = new HarvestCandidateSource(sourceMessageId,
            sourceConversationId,
            "Original Conversation",
            [new HarvestTurn("user", "How do I reset?"), new HarvestTurn("assistant", "Use the reset button.")],
            "Hold the reset button for five seconds.");
        harness.WithSources(source);

        var captured = harness.CaptureCreateHarvested();

        var outcome = await harness.Service.HarvestAsync(AgentId).ConfigureAwait(false);

        AssertEx.True(outcome.AgentExists);
        AssertEx.Equal(expected: 1, outcome.ThumbsUpScanned);
        AssertEx.Equal(expected: 1, outcome.CreatedCount);
        AssertEx.Equal(expected: 0, outcome.DuplicateCount);
        AssertEx.Equal(expected: 0, outcome.SkippedCount);

        var input = AssertEx.NotNull(captured.Value, "CreateHarvestedAsync should have received the candidate.");
        AssertEx.Equal(GoldenConversationSource.Harvested, input.Source);
        AssertEx.False(input.Enabled, "A harvested candidate is staged inert.");
        AssertEx.Equal(sourceMessageId, input.SourceMessageId);
        AssertEx.Equal(sourceConversationId, input.SourceConversationId);

        var rubric = AssertEx.NotNull(input.Rubric, "Harvested candidate should carry a seeded rubric.");
        AssertEx.Contains(rubric, RubricSeedPrefix);
        AssertEx.Contains(rubric, "Hold the reset button for five seconds.");
        AssertEx.Null(input.Assertion, "Harvested candidate seeds a rubric, not an assertion.");

        AssertEx.NotNull(input.Title, "Harvested candidate should carry a title.");
        AssertEx.True(input.Title.StartsWith("Harvested:", StringComparison.Ordinal), "Harvested title should be prefixed.");

        // InputTurns is the camelCase [{role,text}] JSON of the prior turns.
        using var document = JsonDocument.Parse(input.InputTurns);
        var turns = document.RootElement;
        AssertEx.Equal(expected: 2, turns.GetArrayLength());
        AssertEx.Equal("user", turns[0].GetProperty("role").GetString());
        AssertEx.Equal("How do I reset?", turns[0].GetProperty("text").GetString());
        AssertEx.Equal("assistant", turns[1].GetProperty("role").GetString());
    }

    [Test]
    public async Task HarvestAsync_WhenSourceAlreadyHarvested_CountsDuplicateAndDoesNotCreate()
    {
        var harness = new Harness();
        var sourceMessageId = Guid.NewGuid();
        var source = new HarvestCandidateSource(sourceMessageId,
            Guid.NewGuid(),
            "Conv",
            [new HarvestTurn("user", "q")],
            "a");
        harness.WithSources(source);
        // The dedup set already contains this source's message id.
        harness.GoldenStore.ListSourceMessageIdsByAgentAsync(AgentId, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<IReadOnlyList<Guid>>([sourceMessageId]));

        var outcome = await harness.Service.HarvestAsync(AgentId).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, outcome.DuplicateCount);
        AssertEx.Equal(expected: 0, outcome.CreatedCount);
        await harness.ConversationService.DidNotReceive()
                     .CreateHarvestedAsync(Arg.Any<GoldenConversationCreateInput>(), Arg.Any<CancellationToken>())
                     .ConfigureAwait(false);
    }

    [Test]
    public async Task HarvestAsync_WhenSourceHasNoPriorUserTurn_CountsSkippedAndDoesNotCreate()
    {
        var harness = new Harness();
        var source = new HarvestCandidateSource(Guid.NewGuid(),
            Guid.NewGuid(),
            "Conv",
            // Only an assistant prior turn (no lead-up user turn) → unusable as an input conversation.
            [new HarvestTurn("assistant", "answer with no question")],
            "a");
        harness.WithSources(source);

        var outcome = await harness.Service.HarvestAsync(AgentId).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, outcome.SkippedCount);
        AssertEx.Equal(expected: 0, outcome.CreatedCount);
        await harness.ConversationService.DidNotReceive()
                     .CreateHarvestedAsync(Arg.Any<GoldenConversationCreateInput>(), Arg.Any<CancellationToken>())
                     .ConfigureAwait(false);
    }

    [Test]
    public async Task HarvestAsync_WhenMoreSourcesThanMaxProposals_CapsCreatedCount()
    {
        var harness = new Harness(2);
        harness.WithSources(FreshSource("q1"),
            FreshSource("q2"),
            FreshSource("q3"));

        var outcome = await harness.Service.HarvestAsync(AgentId).ConfigureAwait(false);

        AssertEx.Equal(expected: 3, outcome.ThumbsUpScanned);
        AssertEx.Equal(expected: 2, outcome.CreatedCount);
        await harness.ConversationService.Received(2)
                     .CreateHarvestedAsync(Arg.Any<GoldenConversationCreateInput>(), Arg.Any<CancellationToken>())
                     .ConfigureAwait(false);
    }

    [Test]
    public async Task HarvestAsync_WhenCreateRejectsACandidate_CountsSkippedAndContinues()
    {
        var harness = new Harness();
        var rejected = FreshSource("rejected");
        var accepted = FreshSource("accepted");
        harness.WithSources(rejected, accepted);

        // The first candidate is rejected at the create boundary; the harvest must keep going for the rest.
        harness.ConversationService
               .CreateHarvestedAsync(Arg.Is<GoldenConversationCreateInput>(input => input.SourceMessageId == rejected.MessageId), Arg.Any<CancellationToken>())
               .Returns<Task<GoldenConversationRecord>>(_ => throw new PlaybookActionValidationException("over the cap"));
        harness.ConversationService
               .CreateHarvestedAsync(Arg.Is<GoldenConversationCreateInput>(input => input.SourceMessageId == accepted.MessageId), Arg.Any<CancellationToken>())
               .Returns(callInfo => Task.FromResult(StoredRecord(callInfo.Arg<GoldenConversationCreateInput>())));

        var outcome = await harness.Service.HarvestAsync(AgentId).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, outcome.CreatedCount);
        AssertEx.Equal(expected: 1, outcome.SkippedCount);
        AssertEx.Equal(expected: 0, outcome.DuplicateCount);
    }

    private static HarvestCandidateSource FreshSource(string question)
    {
        return new HarvestCandidateSource(Guid.NewGuid(),
            Guid.NewGuid(),
            "Conv",
            [new HarvestTurn("user", question)],
            "answer for " + question);
    }

    private static GoldenConversationRecord StoredRecord(GoldenConversationCreateInput input)
    {
        return new GoldenConversationRecord(Guid.NewGuid(),
            input.AgentDefinitionId,
            input.Title,
            input.InputTurns,
            input.Assertion,
            input.Rubric,
            Enabled: false,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10,
            GoldenConversationSource.Harvested,
            input.SourceMessageId,
            input.SourceConversationId);
    }

    private sealed class Harness
    {
        public Harness(int maxProposals = 10, int maxThumbsUpScan = 50)
        {
            SourceStore = Substitute.For<IGoldenHarvestSourceStore>();
            GoldenStore = Substitute.For<IGoldenConversationStore>();
            ConversationService = Substitute.For<IGoldenConversationService>();
            AgentStore = Substitute.For<IAgentDefinitionStore>();

            AgentStore.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<AgentDefinitionRecord?>(CreateAgent()));
            SourceStore.ListThumbsUpSourcesAsync(AgentId, maxThumbsUpScan, Arg.Any<CancellationToken>())
                       .Returns(Task.FromResult<IReadOnlyList<HarvestCandidateSource>>([]));
            GoldenStore.ListSourceMessageIdsByAgentAsync(AgentId, Arg.Any<CancellationToken>())
                       .Returns(Task.FromResult<IReadOnlyList<Guid>>([]));
            ConversationService.CreateHarvestedAsync(Arg.Any<GoldenConversationCreateInput>(), Arg.Any<CancellationToken>())
                               .Returns(callInfo => Task.FromResult(StoredRecord(callInfo.Arg<GoldenConversationCreateInput>())));

            var options = Options.Create(new GoldenHarvestOptions
            {
                MaxProposals = maxProposals,
                MaxThumbsUpScan = maxThumbsUpScan
            });
            Service = new GoldenHarvestService(SourceStore,
                GoldenStore,
                ConversationService,
                AgentStore,
                options,
                NullLogger<GoldenHarvestService>.Instance);
        }

        public IGoldenHarvestSourceStore SourceStore { get; }

        public IGoldenConversationStore GoldenStore { get; }

        public IGoldenConversationService ConversationService { get; }

        public IAgentDefinitionStore AgentStore { get; }

        public GoldenHarvestService Service { get; }

        public void WithSources(params HarvestCandidateSource[] sources)
        {
            SourceStore.ListThumbsUpSourcesAsync(AgentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
                       .Returns(Task.FromResult<IReadOnlyList<HarvestCandidateSource>>(sources));
        }

        public CapturedInput CaptureCreateHarvested()
        {
            var captured = new CapturedInput();
            ConversationService.CreateHarvestedAsync(Arg.Do<GoldenConversationCreateInput>(input => captured.Value = input), Arg.Any<CancellationToken>())
                               .Returns(callInfo => Task.FromResult(StoredRecord(callInfo.Arg<GoldenConversationCreateInput>())));
            return captured;
        }

        private static AgentDefinitionRecord CreateAgent()
        {
            return new AgentDefinitionRecord(AgentId,
                "Builder",
                Description: null,
                "Base instructions.",
                ModelProfile: null,
                ReasoningEffort: null,
                AgentDefinitionKind.Single,
                [],
                new Dictionary<string, bool>(),
                OrchestrationTopologyJson: null,
                Version: 1,
                CreatedAtUtc: 10,
                UpdatedAtUtc: 10);
        }
    }

    private sealed class CapturedInput
    {
        public GoldenConversationCreateInput? Value { get; set; }
    }
}
