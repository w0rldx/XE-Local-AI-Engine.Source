namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class BenchmarkJudgePolicyRevisionConfiguration : IEntityTypeConfiguration<BenchmarkJudgePolicyRevision>
{
    public void Configure(EntityTypeBuilder<BenchmarkJudgePolicyRevision> builder)
    {
        builder.ToTable("benchmark_judge_policy_revisions", table =>
        {
            table.HasCheckConstraint("CK_benchmark_judge_policy_revisions_revision", "revision > 0");
            table.HasCheckConstraint("CK_benchmark_judge_policy_revisions_cohort_generation", "cohort_generation > 0");
        });
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.ProjectId).HasColumnName("project_id");
        builder.Property(entity => entity.Revision).HasColumnName("revision");
        builder.Property(entity => entity.PolicyJson).HasColumnName("policy_json").IsRequired();
        builder.Property(entity => entity.PolicyHash).HasColumnName("policy_hash").HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.ReferenceExecutionKey).HasColumnName("reference_execution_key").HasMaxLength(64);
        builder.Property(entity => entity.CohortGeneration).HasColumnName("cohort_generation");
        builder.Property(entity => entity.ComparisonSetVersion).HasColumnName("comparison_set_version").HasDefaultValue(0);
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.HasOne<BenchmarkProject>().WithMany().HasForeignKey(entity => entity.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(entity => new
        {
            entity.ProjectId,
            entity.Revision
        }).IsUnique().HasDatabaseName("ux_benchmark_judge_policy_revisions_project_revision");

        // One row per distinct policy per project: get-or-create is insert, and on this conflict re-query, so
        // r1 -> r2 -> r1 deterministically repoints at the original row instead of minting a duplicate.
        builder.HasIndex(entity => new
        {
            entity.ProjectId,
            entity.PolicyHash
        }).IsUnique().HasDatabaseName("ux_benchmark_judge_policy_revisions_project_policy_hash");
    }
}
