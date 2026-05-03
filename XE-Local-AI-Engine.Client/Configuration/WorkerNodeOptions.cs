namespace XE_Local_AI_Engine.Client.Configuration;

using System.ComponentModel.DataAnnotations;

public sealed class WorkerNodeOptions
{
    public const string SectionName = "WorkerNode";

    [Required]
    public required string NodeName { get; set; }

    [Range(1, 100)]
    public int MaxResponseSizeMb { get; set; } = 10;

    public string DeadLetterQueuePath { get; set; } = "dead-letter-queue";

    [Range(1, 60)]
    public int MaxPendingToolCallAgeMinutes { get; set; } = 10;

    [Range(1, 3600)]
    public int CleanupIntervalSeconds { get; set; } = 60;
}
