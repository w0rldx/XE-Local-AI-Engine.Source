namespace XE_Local_AI_Engine.Client.Services.Training.Runs;

using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

/// <summary>Versioned canonical JSONL codec for the immutable corpus shared by training and evaluation.</summary>
internal static class FrozenTrainingCorpus
{
    public static ReadOnlyMemory<byte> Write(IEnumerable<TrainingSampleRecord> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var builder = new StringBuilder();
        foreach (var sample in samples.Where(item => item.ReviewState != TrainingSampleReviewState.Rejected))
        {
            var content = JsonSerializer.Deserialize<TrainingSampleContentV1>(sample.ContentJson.Span, TrainingJson.Options);
            if (content is null)
            {
                continue;
            }

            _ = builder.AppendLine(WriteLine(sample, content));
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public static IReadOnlyList<TrainingSampleRecord> Read(ReadOnlySpan<byte> plaintext, TrainingRunFreezeV1 freeze)
    {
        ArgumentNullException.ThrowIfNull(freeze);
        var legacyIdsBySequence = freeze.HoldoutSampleIds.Count == freeze.HoldoutSequences.Count
            ? freeze.HoldoutSequences.Select((sequence, index) => (sequence, id: freeze.HoldoutSampleIds[index])).ToDictionary(item => item.sequence, item => item.id)
            : new Dictionary<int, Guid>();
        var records = new List<TrainingSampleRecord>();
        foreach (var line in Encoding.UTF8.GetString(plaintext).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var sequence = root.GetProperty("sequence").GetInt32();
            Guid id;
            if (root.TryGetProperty("sampleId", out var sampleIdElement))
            {
                if (!root.TryGetProperty("schemaVersion", out var schemaVersion)
                    || schemaVersion.GetInt32() != TrainingRunFreezeV1.CurrentSchemaVersion)
                {
                    throw new InvalidOperationException("The frozen corpus line has an unsupported schema version.");
                }

                id = sampleIdElement.GetGuid();
            }
            else if (freeze.SchemaVersion > 1 || !legacyIdsBySequence.TryGetValue(sequence, out id))
            {
                // Legacy v1 did not embed ids. Only holdout rows have a durable sequence→id map and evaluation only
                // consumes holdout rows, so unrelated training rows are intentionally ignored during compatibility read.
                continue;
            }

            var content = new TrainingSampleContentV1
            {
                SystemInstructions = root.TryGetProperty("systemInstructions", out var instructions)
                    ? instructions.GetString() ?? string.Empty
                    : string.Empty,
                Parts = root.GetProperty("parts").Deserialize<IReadOnlyList<TrainingSamplePartV1>>(TrainingJson.Options) ?? []
            };
            var label = Enum.Parse<TrainingSampleLabel>(root.GetProperty("label").GetString()!, ignoreCase: true);
            var reviewState = Enum.Parse<TrainingSampleReviewState>(root.GetProperty("reviewState").GetString()!, ignoreCase: true);
            records.Add(new TrainingSampleRecord(id,
                Guid.Empty,
                sequence,
                root.GetProperty("kind").GetString() ?? string.Empty,
                label,
                reviewState,
                JsonSerializer.SerializeToUtf8Bytes(content, TrainingJson.Options),
                ValidationJson: null,
                TrainingSampleProvenance.Generated,
                SourceHash: string.Empty,
                CreatedAtUtc: 0,
                UpdatedAtUtc: 0));
        }

        return records;
    }

    public static string WriteLine(TrainingSampleRecord sample, TrainingSampleContentV1 content) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = TrainingRunFreezeV1.CurrentSchemaVersion,
            sampleId = sample.Id,
            sequence = sample.Sequence,
            kind = sample.Kind,
            label = sample.Label.ToString(),
            reviewState = sample.ReviewState.ToString(),
            systemInstructions = content.SystemInstructions,
            parts = content.Parts
        }, TrainingJson.Options);
}
