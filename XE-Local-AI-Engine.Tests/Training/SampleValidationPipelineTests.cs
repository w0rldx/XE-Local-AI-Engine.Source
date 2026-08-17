namespace XE_Local_AI_Engine.Tests.Training;

using System.Text.Json;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Tests.Testing;

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
                    .Returns(new HeadlessToolOutcome(HeadlessToolOutcomeKind.Executed, "# Title", "read-local"));

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
                    .Returns(new HeadlessToolOutcome(HeadlessToolOutcomeKind.ValidationOnly, null, "not-read-local; no mock matched"));

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

    private static ISampleValidationPipeline Create(out IHeadlessToolExecutor executor)
    {
        executor = Substitute.For<IHeadlessToolExecutor>();
        _ = executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns(new HeadlessToolOutcome(HeadlessToolOutcomeKind.Executed, "ok", "read-local"));
        return new SampleValidationPipeline(executor, Substitute.For<IStructuredAgentRunner>());
    }

    private static SampleValidationContext Context() =>
        new(new DatasetDefinitionBodyV1
        {
            TeacherModelName = "teacher.gguf",
            TeacherOutputMode = TeacherOutputMode.ValidateAfter,
            SystemInstructions = "produce examples",
            Tools = [new DatasetToolSnapshotV1("read_file", "Reads a file.", ToolSchema, RequiresApproval: false, ToolCategory.ReadLocal)]
        }, "tool-call", TrainingSampleLabel.Good, RecordSchema, CriticChatClient: null);
}
