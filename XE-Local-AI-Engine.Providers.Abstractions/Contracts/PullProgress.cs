namespace XE_Local_AI_Engine.Providers.Abstractions.Contracts;

using System.Text.Json.Serialization;

public sealed record PullProgress
{
    [JsonRequired]
    public required string ModelName { get; init; }

    [JsonRequired]
    public required string Status { get; init; }

    [JsonRequired]
    public required long? TotalBytes { get; init; }

    [JsonRequired]
    public required long? CompletedBytes { get; init; }

    /// <summary>
    ///     1-based index of the file currently transferring within a multi-file set, or <see langword="null" /> for a
    ///     single-file pull. An image model is a <b>set</b> (diffusion + VAE + text encoders), so without this the UI
    ///     cannot tell "the bar restarted because part 2 began" from "the bar restarted because something went wrong".
    /// </summary>
    public int? PartIndex { get; init; }

    /// <summary>Number of files in the set, or <see langword="null" /> for a single-file pull.</summary>
    public int? PartCount { get; init; }
}
