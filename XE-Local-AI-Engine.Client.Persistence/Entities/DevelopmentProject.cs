namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class DevelopmentProject
{
    public Guid Id { get; set; }
    public byte[] Objective { get; set; } = [];
    public Guid? SelectedFolderId { get; set; }
    public string RepositoryIdentityHash { get; set; } = string.Empty;
    public string BaseBranch { get; set; } = string.Empty;
    public DevelopmentProjectStatus Status { get; set; }
    public DevelopmentEgressPolicy EgressPolicy { get; set; }
    public string? CoderModelId { get; set; }
    public string? ReviewerModelId { get; set; }
    public int? MaxTokens { get; set; }
    public int? MaxDurationSeconds { get; set; }
    public int ConfigurationVersion { get; set; }
    public bool TrustedRepositoryAcknowledged { get; set; }
    public int? TrustedRepositoryPolicyVersion { get; set; }
    public long? TrustedRepositoryAcknowledgedAtUtc { get; set; }
    public long CreatedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
    public long Version { get; set; }
}
