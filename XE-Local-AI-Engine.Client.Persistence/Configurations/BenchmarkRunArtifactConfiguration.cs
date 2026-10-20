namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class BenchmarkRunArtifactConfiguration : IEntityTypeConfiguration<BenchmarkRunArtifact>
{
    public void Configure(EntityTypeBuilder<BenchmarkRunArtifact> builder)
    {
        builder.ToTable("benchmark_run_artifacts", table =>
        {
            table.HasCheckConstraint("CK_benchmark_run_artifacts_kind", "kind IN ('pythonTestsStdout', 'pythonTestsStderr')");
            table.HasCheckConstraint("CK_benchmark_run_artifacts_byte_count", "byte_count >= 0");
        });
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.RunId).HasColumnName("run_id");
        builder.Property(entity => entity.JudgeAttemptId).HasColumnName("judge_attempt_id");
        builder.Property(entity => entity.CriterionId).HasColumnName("criterion_id").HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.Kind).HasColumnName("kind").HasMaxLength(32).IsRequired();
        builder.Property(entity => entity.MediaType).HasColumnName("media_type").HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.ByteCount).HasColumnName("byte_count");
        builder.Property(entity => entity.ContentHash).HasColumnName("content_hash").HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.HasOne<BenchmarkRun>().WithMany().HasForeignKey(entity => entity.RunId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BenchmarkJudgeAttempt>().WithMany().HasForeignKey(entity => entity.JudgeAttemptId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(entity => entity.RunId).HasDatabaseName("ix_benchmark_run_artifacts_run_id");
        builder.HasIndex(entity => new
        {
            entity.JudgeAttemptId,
            entity.CriterionId,
            entity.Kind
        }).IsUnique().HasDatabaseName("ux_benchmark_run_artifacts_attempt_criterion_kind");
    }
}
