namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class TrainingComparisonReportConfiguration : IEntityTypeConfiguration<TrainingComparisonReport>
{
    public void Configure(EntityTypeBuilder<TrainingComparisonReport> builder)
    {
        builder.ToTable("training_comparison_reports");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.Name).HasColumnName("name").HasMaxLength(255).IsRequired();
        builder.Property(entity => entity.BaseEvaluationRunId).HasColumnName("base_evaluation_run_id");
        builder.Property(entity => entity.TunedEvaluationRunId).HasColumnName("tuned_evaluation_run_id");
        builder.Property(entity => entity.BaseBenchmarkRunId).HasColumnName("base_benchmark_run_id");
        builder.Property(entity => entity.TunedBenchmarkRunId).HasColumnName("tuned_benchmark_run_id");
        builder.Property(entity => entity.TrainingRunId).HasColumnName("training_run_id");
        builder.Property(entity => entity.DeltasJson).HasColumnName("deltas_json").IsRequired();
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");

        // Restricted on the two evaluations: a report whose inputs vanished would carry deltas nothing can reproduce.
        // The benchmark ids carry no foreign key on purpose — a benchmark run has its own lifecycle and deleting one
        // must degrade the report's throughput section, not block the delete.
        builder.HasOne<TrainingEvaluationRun>()
               .WithMany()
               .HasForeignKey(entity => entity.BaseEvaluationRunId)
               .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TrainingEvaluationRun>()
               .WithMany()
               .HasForeignKey(entity => entity.TunedEvaluationRunId)
               .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(entity => entity.TrainingRunId).HasDatabaseName("ix_training_comparison_reports_training_run");
    }
}
