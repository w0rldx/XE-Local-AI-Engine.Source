namespace XE_Local_AI_Engine.Client.Persistence.Entities;

public sealed class DevelopmentTask
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public byte[] Title { get; set; } = [];

    public byte[] Requirements { get; set; } = [];

    public byte[] AcceptanceCriteriaJson { get; set; } = [];

    public DevelopmentTaskStatus Status { get; set; }

    public int CurrentReviewRound { get; set; }

    public int MaximumReviewRounds { get; set; } = 3;

    public string? BlockedReason { get; set; }

    public string? CurrentApprovedSubjectHash { get; set; }

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }

    public long Version { get; set; }
}
