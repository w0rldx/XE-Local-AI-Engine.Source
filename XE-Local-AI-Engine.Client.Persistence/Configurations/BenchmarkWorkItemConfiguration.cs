namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class BenchmarkWorkItemConfiguration : IEntityTypeConfiguration<BenchmarkWorkItem>
{
    public void Configure(EntityTypeBuilder<BenchmarkWorkItem> builder)
    {
        builder.ToTable("benchmark_work_items", table =>
        {
            table.HasCheckConstraint("CK_benchmark_work_items_attempt", "attempt = 1");
            // One arm per kind, and each arm names EVERY id column: an arm that only constrained its own column
            // would let a Fidelity row also carry a comparison id, i.e. one work item claiming to be two things.
            table.HasCheckConstraint("CK_benchmark_work_items_judge_attempt",
                "(kind = 'Primary' AND judge_attempt_id IS NULL AND comparison_id IS NULL AND fidelity_attempt_id IS NULL)"
                + " OR (kind = 'Judge' AND judge_attempt_id IS NOT NULL AND comparison_id IS NULL AND fidelity_attempt_id IS NULL)"
                + " OR (kind = 'Fidelity' AND judge_attempt_id IS NULL AND comparison_id IS NULL AND fidelity_attempt_id IS NOT NULL)"
                + " OR (kind = 'Comparison' AND judge_attempt_id IS NULL AND comparison_id IS NOT NULL AND fidelity_attempt_id IS NULL)");
        });
        builder.HasKey(entity => entity.QueueSequence);
        builder.Property(entity => entity.QueueSequence).HasColumnName("queue_sequence").ValueGeneratedOnAdd();
        builder.Property(entity => entity.RunId).HasColumnName("run_id");
        builder.Property(entity => entity.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16);
        builder.Property(entity => entity.JudgeAttemptId).HasColumnName("judge_attempt_id");
        builder.Property(entity => entity.ComparisonId).HasColumnName("comparison_id");
        builder.Property(entity => entity.FidelityAttemptId).HasColumnName("fidelity_attempt_id");
        builder.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        builder.Property(entity => entity.Attempt).HasColumnName("attempt");
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(entity => entity.EnqueuedAtUtc).HasColumnName("enqueued_at_utc");
        builder.Property(entity => entity.StartedAtUtc).HasColumnName("started_at_utc");
        builder.Property(entity => entity.FinishedAtUtc).HasColumnName("finished_at_utc");
        builder.Property(entity => entity.ErrorMessage).HasColumnName("error_message").HasMaxLength(1024);
        builder.HasOne<BenchmarkRun>().WithMany().HasForeignKey(entity => entity.RunId).OnDelete(DeleteBehavior.Restrict);
        // Filtered, not composite: a run has exactly one primary item but one judge item per attempt, and SQLite
        // lets a plain unique index repeat NULLs, so `(run_id, kind)` would not constrain judge work at all.
        builder.HasIndex(entity => entity.RunId)
               .IsUnique()
               .HasFilter("kind = 'Primary'")
               .HasDatabaseName("ux_benchmark_work_items_primary_run");
        builder.HasIndex(entity => entity.JudgeAttemptId)
               .IsUnique()
               .HasFilter("kind = 'Judge'")
               .HasDatabaseName("ux_benchmark_work_items_judge_attempt");
        builder.HasIndex(entity => entity.ComparisonId)
               .IsUnique()
               .HasFilter("kind = 'Comparison'")
               .HasDatabaseName("ux_benchmark_work_items_comparison");
        builder.HasIndex(entity => entity.FidelityAttemptId)
               .IsUnique()
               .HasFilter("kind = 'Fidelity'")
               .HasDatabaseName("ux_benchmark_work_items_fidelity");
        builder.HasIndex(entity => new
        {
            entity.Status,
            entity.QueueSequence
        }).HasDatabaseName("ix_benchmark_work_items_status_sequence");
    }
}
