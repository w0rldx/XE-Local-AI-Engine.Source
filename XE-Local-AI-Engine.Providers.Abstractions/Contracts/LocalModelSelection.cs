namespace XE_Local_AI_Engine.Providers.Abstractions.Contracts;

using System.Text.Json.Serialization;

public sealed record LocalModelSelection
{
    [JsonRequired]
    public required string ModelName { get; init; }

    [JsonRequired]
    public required string ProviderName { get; init; }

    /// <summary>How long a model this selection loads should stay resident; <see cref="ModelResidencyIntent.Interactive" /> by default.</summary>
    public ModelResidencyIntent ResidencyIntent { get; init; }
}
