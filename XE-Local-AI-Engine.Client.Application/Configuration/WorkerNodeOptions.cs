namespace XE_Local_AI_Engine.Client.Configuration;

using System.ComponentModel.DataAnnotations;

public sealed class WorkerNodeOptions
{
    public const string SectionName = "WorkerNode";

    [Required]
    public required string NodeName { get; set; }

    [Range(minimum: 1, maximum: 100)]
    public int MaxResponseSizeMb { get; set; } = 10;

    public string DeadLetterQueuePath { get; set; } = "dead-letter-queue";

    [Range(minimum: 1, maximum: 60)]
    public int MaxPendingToolCallAgeMinutes { get; set; } = 10;

    [Range(minimum: 1, maximum: 3600)]
    public int CleanupIntervalSeconds { get; set; } = 60;
}
