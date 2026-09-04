namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class BenchmarkJudgeAttemptConfiguration : IEntityTypeConfiguration<BenchmarkJudgeAttempt>
{
    public void Configure(EntityTypeBuilder<BenchmarkJudgeAttempt> builder)
    {
        builder.ToTable("benchmark_judge_attempts", table =>
        {
            table.HasCheckConstraint("CK_benchmark_judge_attempts_sequence", "sequence > 0");
            table.HasCheckConstraint("CK_benchmark_judge_attempts_cohort_generation", "cohort_generation > 0");
            table.HasCheckConstraint("CK_benchmark_judge_attempts_score", "score IS NULL OR (score >= 0 AND score <= 100)");
            table.HasCheckConstraint("CK_benchmark_judge_attempts_status",
                "status IN ('Queued', 'Running', 'Succeeded', 'Failed', 'Cancelled')");
        });
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.RunId).HasColumnName("run_id");
        builder.Property(entity => entity.Sequence).HasColumnName("sequence");
        builder.Property(entity => entity.PolicyRevisionId).HasColumnName("policy_revision_id");
        builder.Property(entity => entity.CohortGeneration).HasColumnName("cohort_generation");
        builder.Property(entity => entity.JudgeRuntimeJson).HasColumnName("judge_runtime_json");
        builder.Property(entity => entity.JudgeExecutionKey).HasColumnName("judge_execution_key").HasMaxLength(64);
        builder.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.ResultJson).HasColumnName("result_json");
        builder.Property(entity => entity.Score).HasColumnName("score");
        builder.Property(entity => entity.ErrorMessage).HasColumnName("error_message").HasMaxLength(1024);
        builder.Property(entity => entity.LaunchReceiptJson).HasColumnName("launch_receipt_json");
        builder.Property(entity => entity.EnvironmentFactsJson).HasColumnName("environment_facts_json");
        builder.Property(entity => entity.Variant).HasColumnName("variant").HasMaxLength(32);
        builder.Property(entity => entity.KvCacheType).HasColumnName("kv_cache_type").HasMaxLength(32);
        builder.Property(entity => entity.KvCacheTypeSource).HasColumnName("kv_cache_type_source").HasMaxLength(16);
        builder.Property(entity => entity.KvAutoReason).HasColumnName("kv_auto_reason").HasMaxLength(64);
        builder.Property(entity => entity.FlashAttentionMode).HasColumnName("flash_attention_mode").HasMaxLength(16);
        builder.Property(entity => entity.IntendedLaunchIdentity).HasColumnName("intended_launch_identity").HasMaxLength(64);
        builder.Property(entity => entity.LaunchIdentityScheme).HasColumnName("launch_identity_scheme");
        builder.Property(entity => entity.IntendedExecutableSha256).HasColumnName("intended_executable_sha256").HasMaxLength(64);
        builder.Property(entity => entity.ReceiptHash).HasColumnName("receipt_hash").HasMaxLength(64);
        builder.Property(entity => entity.EnvironmentFactsHash).HasColumnName("environment_facts_hash").HasMaxLength(64);
        builder.Property(entity => entity.EffectiveLaunchIdentity).HasColumnName("effective_launch_identity").HasMaxLength(64);
        builder.Property(entity => entity.EffectiveBackend).HasColumnName("effective_backend").HasMaxLength(32);
        builder.Property(entity => entity.PlacementOffloaded).HasColumnName("placement_offloaded");
        builder.Property(entity => entity.PlacementTotal).HasColumnName("placement_total");
        builder.Property(entity => entity.LaunchExecutableSha256).HasColumnName("launch_executable_sha256").HasMaxLength(64);
        builder.Property(entity => entity.LaunchHasAuxAssets).HasColumnName("launch_has_aux_assets");
        builder.Property(entity => entity.LaunchKvCacheTypeSource).HasColumnName("launch_kv_cache_type_source").HasMaxLength(16);
        builder.Property(entity => entity.EnqueuedAtUtc).HasColumnName("enqueued_at_utc");
        builder.Property(entity => entity.StartedAtUtc).HasColumnName("started_at_utc");
        builder.Property(entity => entity.CompletedAtUtc).HasColumnName("completed_at_utc");
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.HasOne<BenchmarkRun>().WithMany().HasForeignKey(entity => entity.RunId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BenchmarkJudgePolicyRevision>().WithMany().HasForeignKey(entity => entity.PolicyRevisionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(entity => new
        {
            entity.RunId,
            entity.Sequence
        }).IsUnique().HasDatabaseName("ux_benchmark_judge_attempts_run_sequence");
        builder.HasIndex(entity => new
        {
            entity.RunId,
            entity.Status
        }).HasDatabaseName("ix_benchmark_judge_attempts_run_status");
        builder.HasIndex(entity => new
        {
            entity.RunId,
            entity.JudgeExecutionKey
        }).HasDatabaseName("ix_benchmark_judge_attempts_run_execution_key");
    }
}
