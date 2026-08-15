namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class TrainingEvaluationRunConfiguration : IEntityTypeConfiguration<TrainingEvaluationRun>
{
    public void Configure(EntityTypeBuilder<TrainingEvaluationRun> builder)
    {
        builder.ToTable("training_evaluation_runs",
            table => table.HasCheckConstraint("CK_training_evaluation_runs_counts",
                "total_count >= 0 AND scored_count >= 0 AND passed_count >= 0 AND scored_count <= total_count AND passed_count <= scored_count"));
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.TrainingRunId).HasColumnName("training_run_id");
        builder.Property(entity => entity.ComparisonId).HasColumnName("comparison_id");
        builder.Property(entity => entity.ModelName).HasColumnName("model_name").HasMaxLength(255).IsRequired();
        builder.Property(entity => entity.ModelContentFingerprint).HasColumnName("model_content_fingerprint").HasMaxLength(67);
        builder.Property(entity => entity.DatasetId).HasColumnName("dataset_id");

        // Same width as training_datasets.content_fingerprint — "v1:" plus 64 hex characters.
        builder.Property(entity => entity.DatasetContentFingerprint).HasColumnName("dataset_content_fingerprint").HasMaxLength(67).IsRequired();
        builder.Property(entity => entity.MembershipJson).HasColumnName("membership_json").IsRequired();
        builder.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        builder.Property(entity => entity.ResultsJson).HasColumnName("results_json");
        builder.Property(entity => entity.TotalCount).HasColumnName("total_count");
        builder.Property(entity => entity.ScoredCount).HasColumnName("scored_count");
        builder.Property(entity => entity.PassedCount).HasColumnName("passed_count");
        builder.Property(entity => entity.PerKindJson).HasColumnName("per_kind_json").HasMaxLength(4096);
        builder.Property(entity => entity.ErrorMessage).HasColumnName("error_message").HasMaxLength(1024);
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");

        // Restricted like every other cross-aggregate training reference. The node connection never sets
        // PRAGMA foreign_keys=ON, so these declare the guard the store enforces explicitly rather than enforcing it.
        builder.HasOne<TrainingRun>()
               .WithMany()
               .HasForeignKey(entity => entity.TrainingRunId)
               .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TrainingDataset>()
               .WithMany()
               .HasForeignKey(entity => entity.DatasetId)
               .OnDelete(DeleteBehavior.Restrict);

        // No foreign key on comparison_id: the report points at its two evaluations and the evaluations point back, so
        // declaring both directions would be a cycle SQLite cannot order inserts around.
        builder.HasIndex(entity => entity.TrainingRunId).HasDatabaseName("ix_training_evaluation_runs_training_run");
        builder.HasIndex(entity => entity.ComparisonId).HasDatabaseName("ix_training_evaluation_runs_comparison");
        builder.HasIndex(entity => entity.Status).HasDatabaseName("ix_training_evaluation_runs_status");
    }
}
