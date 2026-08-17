namespace XE_Local_AI_Engine.Tests.Training;

using System.Text;
using System.Text.Json;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class DatasetExportServiceTests
{
    private static readonly Guid DatasetId = Guid.NewGuid();

    private static readonly byte[] DefinitionJson = Encoding.UTF8.GetBytes("""{"schemaVersion":1,"teacherModelName":"teacher.gguf"}""");

    [Test]
    public async Task DatasetExport_HermesConverter_RoundTripsToolCalls()
    {
        var export = Create(Sample(0, TrainingSampleReviewState.Approved));

        var content = await export.ExportAsync(DatasetId, DatasetExportFormat.Hermes);

        var line = content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Single();
        using var document = JsonDocument.Parse(line);
        var conversations = document.RootElement.GetProperty("conversations").EnumerateArray().ToArray();
        AssertEx.Equal("system", conversations[0].GetProperty("from").GetString());
        AssertEx.Equal("human", conversations[1].GetProperty("from").GetString());
        AssertEx.Equal("gpt", conversations[2].GetProperty("from").GetString());
        AssertEx.Equal("tool", conversations[3].GetProperty("from").GetString());
        AssertEx.Equal("# Title", conversations[3].GetProperty("value").GetString());

        // Round trip: the emitted tool_call block parses back to the same name and arguments.
        var parsed = DatasetExportService.TryReadHermesToolCall(conversations[2].GetProperty("value").GetString()!, out var toolName, out var arguments);
        AssertEx.True(parsed, "The Hermes tool_call block must be parseable.");
        AssertEx.Equal("read_file", toolName);
        using var argumentsDocument = JsonDocument.Parse(arguments);
        AssertEx.Equal("README.md", argumentsDocument.RootElement.GetProperty("path").GetString());
    }

    [Test]
    public async Task DatasetExport_Jsonl_IsCanonicalAndTemplateAgnostic()
    {
        var sample = Sample(0, TrainingSampleReviewState.Approved);
        var export = Create(sample);

        var content = await export.ExportAsync(DatasetId, DatasetExportFormat.Jsonl);

        using var document = JsonDocument.Parse(content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Single());
        AssertEx.Equal(expected: 2, document.RootElement.GetProperty("schemaVersion").GetInt32());
        AssertEx.Equal(sample.Id, document.RootElement.GetProperty("sampleId").GetGuid());
        AssertEx.Equal(expected: 0, document.RootElement.GetProperty("sequence").GetInt32());
        AssertEx.Equal("tool-call", document.RootElement.GetProperty("kind").GetString());
        AssertEx.Equal("Good", document.RootElement.GetProperty("label").GetString());
        AssertEx.Equal(expected: 3, document.RootElement.GetProperty("parts").GetArrayLength());
        // No chat template is applied here — that happens inside the trainer.
        AssertEx.False(content.Contains("<|im_start|>", StringComparison.Ordinal));
    }

    [Test]
    public async Task DatasetExport_ExcludesRejectedSamples()
    {
        var export = Create(Sample(0, TrainingSampleReviewState.Approved), Sample(1, TrainingSampleReviewState.Rejected));

        var content = await export.ExportAsync(DatasetId, DatasetExportFormat.Jsonl);

        AssertEx.Equal(expected: 1, content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    private static IDatasetExportService Create(params TrainingSampleRecord[] samples)
    {
        var store = Substitute.For<ITrainingDatasetStore>();
        _ = store.GetDatasetAsync(DatasetId, Arg.Any<CancellationToken>())
                 .Returns(new TrainingDatasetRecord(DatasetId, Guid.NewGuid(), 1, DefinitionJson, "dataset", TrainingDatasetStatus.Ready, 2, "v1:abc",
                     samples.Length, samples.Length, 0, 0, 0, 1, 0, 0, DatasetGenerationWorkStatus.Succeeded, null));
        _ = store.ListAllSamplesAsync(DatasetId, Arg.Any<CancellationToken>()).Returns<IReadOnlyList<TrainingSampleRecord>>(samples);
        return new DatasetExportService(store);
    }

    private static TrainingSampleRecord Sample(int sequence, TrainingSampleReviewState reviewState)
    {
        var content = new TrainingSampleContentV1
        {
            SystemInstructions = "You call tools.",
            Parts =
            [
                new TrainingSamplePartV1("user", 0, "read the readme"),
                new TrainingSamplePartV1("tool", 1, ToolCallId: "generated-1", ToolName: "read_file",
                    Arguments: """{"path":"README.md"}""", Result: "# Title", IsError: false),
                new TrainingSamplePartV1("text", 2, "Here is the readme.")
            ]
        };
        return new TrainingSampleRecord(Guid.NewGuid(), DatasetId, sequence, "tool-call", TrainingSampleLabel.Good, reviewState,
            JsonSerializer.SerializeToUtf8Bytes(content, TrainingJson.Options), null, TrainingSampleProvenance.Generated,
            $"hash-{sequence}", CreatedAtUtc: 0, UpdatedAtUtc: 0);
    }
}
