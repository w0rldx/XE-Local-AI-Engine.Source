namespace XE_Local_AI_Engine.Tests.Training;

using System.Text.Json;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class SampleValidationPipelineTests
{
    private const string ToolSchema = """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""";

    private static readonly JsonElement RecordSchema = JsonDocument.Parse("""
                                                                          {
                                                                            "type": "object",
                                                                            "properties": {
                                                                              "userMessage": { "type": "string" },
                                                                              "assistantText": { "type": "string" },
                                                                              "toolName": { "type": "string" },
                                                                              "toolArgumentsJson": { "type": "string" }
                                                                            },
                                                                            "required": ["userMessage", "assistantText"]
                                                                          }
                                                                          """).RootElement.Clone();

    [Test]
    public async Task ValidateAfter_SchemaInvalidTurn_RecordedAsSampleFailure()
    {
        var pipeline = Create(out _);

        // "userMessage" is missing — the ORIGINAL schema requires it, so the turn cannot become a sample.
        var outcome = await pipeline.ValidateAsync("""prose then {"assistantText":"there"} trailing""", Context());

        AssertEx.False(outcome.Accepted, "A record that fails the original schema is a rejection, not a sample.");
        AssertEx.NotNullOrEmpty(outcome.RejectionReason);
        var layer = outcome.Validation.Layers.Single();
        AssertEx.Equal("record-schema", layer.Layer);
        AssertEx.False(layer.Passed, "The failing layer's outcome is persisted, not swallowed.");
    }

    [Test]
    public async Task ValidateAfter_UnparseableCompletion_IsRecordedNeverThrown()
    {
        var pipeline = Create(out _);

        var outcome = await pipeline.ValidateAsync("the model refused to answer", Context());

        AssertEx.False(outcome.Accepted);
        AssertEx.Contains(outcome.Validation.Layers, layer => layer.Layer == "record-schema" && !layer.Passed);
    }

    [Test]
    public async Task ValidTurn_PersistsEveryLayerOutcomeAndKeepsTheRequestedLabel()
    {
        var pipeline = Create(out var executor);
        _ = executor.ExecuteAsync("read_file", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns(new HeadlessToolOutcome
                    {
                        Kind = HeadlessToolOutcomeKind.Executed,
                        Result = "# Title",
                        Reason = "read-local"
                    });

        var outcome = await pipeline.ValidateAsync("""{"userMessage":"read the readme","assistantText":"done","toolName":"read_file","toolArgumentsJson":"{\"path\":\"README.md\"}"}""",
            Context());

        AssertEx.True(outcome.Accepted);
        AssertEx.True(outcome.Validation.Passed);
        AssertEx.Equal(TrainingSampleLabel.Good, outcome.Label);
        var layers = outcome.Validation.Layers.Select(layer => layer.Layer).ToArray();
        AssertEx.Contains(layers, "record-schema");
        AssertEx.Contains(layers, "tool-name");
        AssertEx.Contains(layers, "arguments");
        AssertEx.Contains(layers, "execution");
        AssertEx.Contains(layers, "critic");
        var parts = AssertEx.NotNull(outcome.Content).Parts;
        AssertEx.Contains(parts, part => part.Kind == "tool" && part.Result == "# Title");
    }

    [Test]
    public async Task SchemaValidTurnThatFailsALaterLayer_IsRetainedAsBadTrainingData()
    {
        var pipeline = Create(out var executor);
        _ = executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns(new HeadlessToolOutcome
                    {
                        Kind = HeadlessToolOutcomeKind.ValidationOnly,
                        Result = null,
                        Reason = "not-read-local; no mock matched"
                    });

        var outcome = await pipeline.ValidateAsync("""{"userMessage":"read the readme","assistantText":"done","toolName":"read_file","toolArgumentsJson":"{\"path\":\"README.md\"}"}""",
            Context());

        AssertEx.True(outcome.Accepted, "Decision #9: a failed layer keeps the sample as negative data.");
        AssertEx.Equal(TrainingSampleLabel.Bad, outcome.Label);
        AssertEx.False(outcome.Validation.Passed);
        AssertEx.Contains(outcome.Validation.Layers, layer => layer.Layer == "execution" && !layer.Passed);
    }

    [Test]
    public async Task UnknownToolName_FailsTheResolutionLayer()
    {
        var pipeline = Create(out _);

        var outcome = await pipeline.ValidateAsync("""{"userMessage":"hi","assistantText":"done","toolName":"delete_everything","toolArgumentsJson":"{}"}""",
            Context());

        AssertEx.True(outcome.Accepted);
        AssertEx.Equal(TrainingSampleLabel.Bad, outcome.Label);
        AssertEx.Contains(outcome.Validation.Layers, layer => layer.Layer == "tool-name" && !layer.Passed);
    }

    [Test]
    [Arguments("")]
    [Arguments("None")]
    [Arguments("none")]
    [Arguments("None required")]
    [Arguments("no tool")]
    [Arguments("null")]
    public async Task NoToolSentinel_IsANoToolAnswer_NotAnUnknownTool(string sentinel)
    {
        // Live-found: constrained decoding forces a value for toolName (the adapter makes every property required), so a
        // small teacher writes "None" for a no-tool answer — that must read as no tool, not as an unresolvable tool name.
        var pipeline = Create(out var executor);

        var outcome = await pipeline.ValidateAsync($$"""{"userMessage":"How are you?","assistantText":"Fine, thanks.","toolName":"{{sentinel}}","toolArgumentsJson":""}""",
            Context());

        AssertEx.True(outcome.Accepted);
        AssertEx.Equal(TrainingSampleLabel.Good, outcome.Label);
        AssertEx.Contains(outcome.Validation.Layers, layer => layer.Layer == "tool-name" && layer.Passed);
        await executor.DidNotReceive().ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SingleCallIsTheSampleBoundary_AndAMultiCallTrajectoryIsRejected()
    {
        var pipeline = Create(out _);

        // What today's teacher record can express: exactly one call. The accepted sample must carry exactly one tool
        // part, which is what makes the boundary rule below hold for everything generation persists.
        var outcome = await pipeline.ValidateAsync("""{"userMessage":"read the readme","assistantText":"done","toolName":"read_file","toolArgumentsJson":"{\"path\":\"README.md\"}"}""",
            Context());

        AssertEx.True(outcome.Accepted);
        AssertEx.Equal(expected: 1, TrainingSampleParts.ToolCalls(AssertEx.NotNull(outcome.Content).Parts).Count);

        // The rule the pipeline enforces over what it built. TeacherSampleRecordV1 carries ONE toolName, so a second
        // call can only appear if that record shape is later widened — and this is what makes that a visible rejection
        // rather than a sample the scorer grades by its first call.
        AssertEx.True(TrainingSampleParts.IsMultiCall([
                new TrainingSamplePartV1("user", 0, "do both"),
                new TrainingSamplePartV1("tool", 1, ToolName: "read_file", Arguments: "{}"),
                new TrainingSamplePartV1("tool", 2, ToolName: "read_file", Arguments: "{}")
            ]),
            "Two named tool parts are a multi-call trajectory.");
        AssertEx.False(TrainingSampleParts.IsMultiCall([
                new TrainingSamplePartV1("tool", 0, ToolName: "read_file", Arguments: "{}"),
                new TrainingSamplePartV1("tool", 1, Result: "ok")
            ]),
            "An unnamed tool part is a result echo, not a second call.");
    }

    [Test]
    public async Task ReasoningStyleCompletion_ThinkBlockProseAndFence_YieldsTheRecord()
    {
        // F-57: a reasoning teacher's answer can carry an inline think block and prose, both with braces of their own.
        const string completion = """
                                  <think>The user wants a {kind} example. Draft: {"userMessage": "draft"} no, better one below.</think>
                                  Here is the record you asked for (format: {json}):
                                  ```json
                                  {"userMessage":"Which river flows through Cairo?","assistantText":"The Nile flows through Cairo.","toolName":"","toolArgumentsJson":""}
                                  ```
                                  Let me know if you need {more}.
                                  """;
        var pipeline = Create(out _);

        var outcome = await pipeline.ValidateAsync(completion, Context());

        AssertEx.True(outcome.Accepted, outcome.RejectionReason ?? "accepted");
        AssertEx.Equal("Which river flows through Cairo?", AssertEx.NotNull(outcome.Content).Parts[0].Content);
    }

    [Test]
    public async Task SchemaFailure_NamesThePropertiesTheRecordCarried()
    {
        var pipeline = Create(out _);

        var outcome = await pipeline.ValidateAsync("""{"question":"q","answer":"a"}""", Context());

        AssertEx.False(outcome.Accepted);
        AssertEx.Contains(outcome.RejectionReason!, "userMessage");
        AssertEx.Contains(outcome.RejectionReason!, "The record carried: question, answer.");
    }

    [Test]
    [Arguments("Produce training example 11 of kind 'rivers'.")]
    [Arguments("  produce training example 15 of kind 'rivers'. It must demonstrate correct behaviour.")]
    public async Task UserTurnThatEchoesTheGenerationInstruction_IsRejected(string userMessage)
    {
        // F-58: a 7B teacher copied its own instruction in as the user turn and every layer reported passed.
        var pipeline = Create(out _);

        var outcome = await pipeline.ValidateAsync(Record(userMessage), Context());

        AssertEx.False(outcome.Accepted);
        AssertEx.Equal("The user message repeats the generation instruction instead of posing a request.", outcome.RejectionReason!);
        AssertEx.Contains(outcome.Validation.Layers, layer => layer.ScoredBy == "critic:deterministic" && !layer.Passed);
    }

    [Test]
    public async Task UserTurnAboutProducingExamples_IsNotAnEcho()
    {
        var pipeline = Create(out _);

        var outcome = await pipeline.ValidateAsync(Record("How do I produce training examples for my model?"), Context());

        AssertEx.True(outcome.Accepted);
    }

    [Test]
    public async Task DuplicateUserTurnWithinOneGeneration_IsRejected_AfterNormalisation()
    {
        // F-58: the same teacher produced "What is the capital of France?" seven times out of eight.
        var pipeline = Create(out _);
        var context = Context();

        var first = await pipeline.ValidateAsync(Record("What is the capital of France?"), context);
        var repeat = await pipeline.ValidateAsync(Record("  what is  the capital\nof FRANCE? "), context);
        var otherRun = await pipeline.ValidateAsync(Record("What is the capital of France?"), Context());

        AssertEx.True(first.Accepted);
        AssertEx.False(repeat.Accepted);
        AssertEx.Equal("The user message duplicates one already accepted in this generation with the same label.", repeat.RejectionReason!);
        AssertEx.True(otherRun.Accepted, "The duplicate check is scoped to one generation's set.");
    }

    [Test]
    public async Task SharedUserTurnAcrossLabels_IsAContrastivePair_AndADemotedSampleCountsUnderBad()
    {
        var pipeline = Create(out _);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var good = await pipeline.ValidateAsync(Record("Read the readme"), Context(TrainingSampleLabel.Good, seen));
        var bad = await pipeline.ValidateAsync(Record("Read the readme"), Context(TrainingSampleLabel.Bad, seen));

        AssertEx.True(good.Accepted);
        AssertEx.True(bad.Accepted, "A Good and a Bad sample may share one prompt.");

        // Requested Good but demoted to Bad by the tool-name layer: it must be keyed under the label it ends with.
        var demotedSeen = new HashSet<string>(StringComparer.Ordinal);
        var demoted = await pipeline.ValidateAsync(
            """{"userMessage":"Delete it","assistantText":"done","toolName":"delete_everything","toolArgumentsJson":"{}"}""",
            Context(TrainingSampleLabel.Good, demotedSeen));
        var laterBad = await pipeline.ValidateAsync(Record("Delete it"), Context(TrainingSampleLabel.Bad, demotedSeen));
        var laterGood = await pipeline.ValidateAsync(Record("Delete it"), Context(TrainingSampleLabel.Good, demotedSeen));

        AssertEx.Equal(TrainingSampleLabel.Bad, demoted.Label);
        AssertEx.False(laterBad.Accepted, "The demoted sample already holds the Bad slot for this prompt.");
        AssertEx.True(laterGood.Accepted, "The Good slot for this prompt is still free.");
    }

    [Test]
    [Arguments("""{"userMessage":"Use {curly} and \"quotes\" in JSON?","assistantText":"Yes."}""", "Use {curly} and \"quotes\" in JSON?")]
    [Arguments("""Here { is the record: {"userMessage":"Q after a stray brace?","assistantText":"A."}""", "Q after a stray brace?")]
    [Arguments("""{"userMessage":"What does </think> mean?","assistantText":"A closing tag."}""", "What does </think> mean?")]
    [Arguments("""<think>plan {draft}</think>{"userMessage":"What does </think> mean?","assistantText":"A tag."}""", "What does </think> mean?")]
    public async Task Extraction_EdgeCases_YieldTheRecordIntact(string completion, string expectedUserMessage)
    {
        // A literal closing think tag inside a record string is preserved: only a LEADING think block is ever dropped.
        var pipeline = Create(out _);

        var outcome = await pipeline.ValidateAsync(completion, Context());

        AssertEx.True(outcome.Accepted, outcome.RejectionReason ?? "accepted");
        AssertEx.Equal(expectedUserMessage, AssertEx.NotNull(outcome.Content).Parts[0].Content);
    }

    [Test]
    [Arguments("I cannot help with that.", "The completion contains no JSON object.")]
    [Arguments("<think>only thinking {\"userMessage\":\"draft\"}</think> and then nothing", "The completion contains no JSON object.")]
    [Arguments("""{"draft":{"userMessage":"What is 2+2?","assistantText":"5"},"final":""", "The completion's JSON record is incomplete.")]
    [Arguments("""Here is the record: {"userMessage":"Which river is longest?","assistantText":"The Ni""", "The completion's JSON record is incomplete.")]
    public async Task Extraction_WithoutARecord_IsRejectedWithAReason(string completion, string expectedReason)
    {
        var pipeline = Create(out _);

        var outcome = await pipeline.ValidateAsync(completion, Context());

        AssertEx.False(outcome.Accepted);
        AssertEx.Equal(expectedReason, outcome.RejectionReason!);
    }

    private static string Record(string userMessage) =>
        JsonSerializer.Serialize(new
        {
            userMessage,
            assistantText = "Paris.",
            toolName = "",
            toolArgumentsJson = ""
        });

    private static ISampleValidationPipeline Create(out IHeadlessToolExecutor executor)
    {
        executor = Substitute.For<IHeadlessToolExecutor>();
        _ = executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns(new HeadlessToolOutcome
                    {
                        Kind = HeadlessToolOutcomeKind.Executed,
                        Result = "ok",
                        Reason = "read-local"
                    });
        return new SampleValidationPipeline(executor, Substitute.For<IStructuredAgentRunner>());
    }

    private static SampleValidationContext Context() =>
        Context(TrainingSampleLabel.Good, new HashSet<string>(StringComparer.Ordinal));

    private static SampleValidationContext Context(TrainingSampleLabel requestedLabel, ISet<string> acceptedUserMessages) =>
        new()
        {
            Definition = new DatasetDefinitionBodyV1
            {
                TeacherModelName = "teacher.gguf",
                TeacherOutputMode = TeacherOutputMode.ValidateAfter,
                SystemInstructions = "produce examples",
                Tools = [new DatasetToolSnapshotV1("read_file", "Reads a file.", ToolSchema, RequiresApproval: false, ToolCategory.ReadLocal)]
            },
            Kind = "tool-call",
            RequestedLabel = requestedLabel,
            RecordSchema = RecordSchema,
            CriticChatClient = null,
            AcceptedUserMessages = acceptedUserMessages
        };
}
