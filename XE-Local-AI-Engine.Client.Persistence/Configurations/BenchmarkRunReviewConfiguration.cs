namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class BenchmarkRunReviewConfiguration : IEntityTypeConfiguration<BenchmarkRunReview>
{
    public void Configure(EntityTypeBuilder<BenchmarkRunReview> builder)
    {
        builder.ToTable("benchmark_run_reviews", table =>
        {
            table.HasCheckConstraint("CK_benchmark_run_reviews_state", "state IN ('approved', 'rejected')");
            table.HasCheckConstraint("CK_benchmark_run_reviews_score", "score IS NULL OR (score >= 0 AND score <= 100)");
            table.HasCheckConstraint("CK_benchmark_run_reviews_approved_score", "state = 'rejected' OR score IS NOT NULL");
        });
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.RunId).HasColumnName("run_id");
        builder.Property(entity => entity.State).HasColumnName("state").HasMaxLength(16).IsRequired();
        builder.Property(entity => entity.Score).HasColumnName("score");
        builder.Property(entity => entity.CriteriaScoresJson).HasColumnName("criteria_scores_json");
        builder.Property(entity => entity.PolicyRevisionId).HasColumnName("policy_revision_id");
        builder.Property(entity => entity.NotesJson).HasColumnName("notes_json");
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.HasOne<BenchmarkRun>().WithMany().HasForeignKey(entity => entity.RunId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(entity => entity.RunId).IsUnique().HasDatabaseName("ux_benchmark_run_reviews_run_id");
        builder.HasIndex(entity => entity.PolicyRevisionId).HasDatabaseName("ix_benchmark_run_reviews_policy_revision_id");
    }
}
