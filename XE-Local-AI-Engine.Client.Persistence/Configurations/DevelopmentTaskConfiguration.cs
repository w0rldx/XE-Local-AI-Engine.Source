namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class DevelopmentTaskConfiguration : IEntityTypeConfiguration<DevelopmentTask>
{
    public void Configure(EntityTypeBuilder<DevelopmentTask> builder)
    {
        builder.ToTable("development_tasks");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.ProjectId).HasColumnName("project_id");
        builder.Property(entity => entity.Title).HasColumnName("title");
        builder.Property(entity => entity.Requirements).HasColumnName("requirements");
        builder.Property(entity => entity.AcceptanceCriteriaJson).HasColumnName("acceptance_criteria_json");
        builder.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.CurrentReviewRound).HasColumnName("current_review_round");
        builder.Property(entity => entity.MaximumReviewRounds).HasColumnName("maximum_review_rounds");
        builder.Property(entity => entity.BlockedReason).HasColumnName("blocked_reason").HasMaxLength(1024);
        builder.Property(entity => entity.CurrentApprovedSubjectHash).HasColumnName("current_approved_subject_hash").HasMaxLength(128);
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.HasIndex(entity => entity.ProjectId).IsUnique();
        builder.HasOne<DevelopmentProject>().WithMany().HasForeignKey(entity => entity.ProjectId).OnDelete(DeleteBehavior.Cascade);
    }
}
