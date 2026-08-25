namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class BenchmarkPairwiseFitConfiguration : IEntityTypeConfiguration<BenchmarkPairwiseFit>
{
    public void Configure(EntityTypeBuilder<BenchmarkPairwiseFit> builder)
    {
        builder.ToTable("benchmark_pairwise_fits", table =>
        {
            table.HasCheckConstraint("CK_benchmark_pairwise_fits_cohort_generation", "cohort_generation > 0");
            table.HasCheckConstraint("CK_benchmark_pairwise_fits_iterations", "iterations > 0 AND bootstrap_replicates > 0");
        });
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.ProjectId).HasColumnName("project_id");
        builder.Property(entity => entity.PolicyRevisionId).HasColumnName("policy_revision_id");
        builder.Property(entity => entity.CohortGeneration).HasColumnName("cohort_generation");
        builder.Property(entity => entity.TaskCaseId).HasColumnName("task_case_id");
        builder.Property(entity => entity.FitKey).HasColumnName("fit_key").HasMaxLength(67).IsRequired();
        builder.Property(entity => entity.JudgeExecutionKey).HasColumnName("judge_execution_key").HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.ComparisonSetVersion).HasColumnName("comparison_set_version");
        builder.Property(entity => entity.FittedSetJson).HasColumnName("fitted_set_json").IsRequired();
        builder.Property(entity => entity.ScoresJson).HasColumnName("scores_json").IsRequired();
        builder.Property(entity => entity.Iterations).HasColumnName("iterations");
        builder.Property(entity => entity.BootstrapReplicates).HasColumnName("bootstrap_replicates");
        builder.Property(entity => entity.IsActive).HasColumnName("is_active").HasDefaultValue(false);
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.HasOne<BenchmarkProject>().WithMany().HasForeignKey(entity => entity.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BenchmarkJudgePolicyRevision>().WithMany().HasForeignKey(entity => entity.PolicyRevisionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(entity => entity.FitKey).IsUnique().HasDatabaseName("ux_benchmark_pairwise_fits_key");
        builder.HasIndex(entity => new
        {
            entity.ProjectId,
            entity.IsActive
        }).HasDatabaseName("ix_benchmark_pairwise_fits_project");

        // ux_benchmark_pairwise_fits_active is DELIBERATELY absent here — see the note in
        // BenchmarkJudgeComparisonConfiguration. It indexes COALESCE(task_case_id, x'00') and is raw SQL in the
        // migration; declaring it here would emit a bare-column index that lets two active fits coexist.
    }
}
