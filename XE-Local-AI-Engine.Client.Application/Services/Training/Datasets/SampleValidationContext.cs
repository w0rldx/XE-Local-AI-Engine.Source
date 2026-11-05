namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>What the pipeline needs beyond the raw completion: the definition it was generated from, and the models to reach.</summary>
public sealed class SampleValidationContext
{
    public required DatasetDefinitionBodyV1 Definition { get; init; }

    public required string Kind { get; init; }

    public required TrainingSampleLabel RequestedLabel { get; init; }

    public required JsonElement RecordSchema { get; init; }

    public required IChatClient? CriticChatClient { get; init; }

    /// <summary>"label|normalized user turn" keys this generation already accepted; the caller owns one set per run.</summary>
    public ISet<string> AcceptedUserMessages { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}
