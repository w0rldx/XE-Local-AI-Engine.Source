namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class DevelopmentTask
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public byte[] Title { get; set; } = [];
    public byte[] Requirements { get; set; } = [];
    public byte[] AcceptanceCriteriaJson { get; set; } = [];
    public DevelopmentTaskStatus Status { get; set; }
    public int CurrentReviewRound { get; set; }
    public int MaxReviewRounds { get; set; }
    public string? BlockedReason { get; set; }
    public long? BlockedAtUtc { get; set; }
    public string? ApprovedSubjectHash { get; set; }
    public long CreatedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
    public long Version { get; set; }
}
