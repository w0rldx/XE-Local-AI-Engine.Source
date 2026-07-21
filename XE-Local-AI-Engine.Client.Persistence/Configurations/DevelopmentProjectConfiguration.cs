namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class DevelopmentProjectConfiguration : IEntityTypeConfiguration<DevelopmentProject>
{
    public void Configure(EntityTypeBuilder<DevelopmentProject> builder)
    {
        builder.ToTable("development_projects");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.Objective).HasColumnName("objective");
        builder.Property(entity => entity.RepositoryIdentityHash).HasColumnName("repository_identity_hash").HasMaxLength(128);
        builder.Property(entity => entity.BaseBranch).HasColumnName("base_branch").HasMaxLength(255);
        builder.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.EgressMode).HasColumnName("egress_mode").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.CoderModelId).HasColumnName("coder_model_id").HasMaxLength(512);
        builder.Property(entity => entity.ReviewerModelId).HasColumnName("reviewer_model_id").HasMaxLength(512);
        builder.Property(entity => entity.MaximumTokens).HasColumnName("maximum_tokens");
        builder.Property(entity => entity.MaximumDurationSeconds).HasColumnName("maximum_duration_seconds");
        builder.Property(entity => entity.FeatureVersion).HasColumnName("feature_version");
        builder.Property(entity => entity.TrustedRepositoryAcknowledged).HasColumnName("trusted_repository_acknowledged");
        builder.Property(entity => entity.TrustedRepositoryPolicyVersion).HasColumnName("trusted_repository_policy_version");
        builder.Property(entity => entity.TrustedRepositoryAcknowledgedAtUtc).HasColumnName("trusted_repository_acknowledged_at_utc");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
    }
}
