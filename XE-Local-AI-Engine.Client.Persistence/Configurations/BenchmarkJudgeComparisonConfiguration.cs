namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class BenchmarkJudgeComparisonConfiguration : IEntityTypeConfiguration<BenchmarkJudgeComparison>
{
    public void Configure(EntityTypeBuilder<BenchmarkJudgeComparison> builder)
    {
        builder.ToTable("benchmark_comparisons", table =>
        {
            table.HasCheckConstraint("CK_benchmark_comparisons_sequence", "sequence > 0 AND attempt_sequence > 0");
            table.HasCheckConstraint("CK_benchmark_comparisons_cohort_generation", "cohort_generation > 0");
            table.HasCheckConstraint("CK_benchmark_comparisons_verdict", "verdict IS NULL OR verdict IN ('a', 'b', 'tie')");
            table.HasCheckConstraint("CK_benchmark_comparisons_status",
                "status IN ('Queued', 'Running', 'Succeeded', 'Failed', 'Cancelled')");

            // The canonical pair ordering is a database invariant, not only a planner convention: a row that named
            // the same two runs the other way round would be a second, undetected slot for one comparison.
            table.HasCheckConstraint("CK_benchmark_comparisons_pair_order", "run_a_id < run_b_id AND \"order\" IN (0, 1)");
        });
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.ProjectId).HasColumnName("project_id");
        builder.Property(entity => entity.PolicyRevisionId).HasColumnName("policy_revision_id");
        builder.Property(entity => entity.CohortGeneration).HasColumnName("cohort_generation");
        builder.Property(entity => entity.TaskCaseId).HasColumnName("task_case_id");
        builder.Property(entity => entity.TaskInputHash).HasColumnName("task_input_hash").HasMaxLength(67).IsRequired();
        builder.Property(entity => entity.RunAId).HasColumnName("run_a_id");
        builder.Property(entity => entity.RunBId).HasColumnName("run_b_id");
        builder.Property(entity => entity.Order).HasColumnName("order");
        builder.Property(entity => entity.AttemptSequence).HasColumnName("attempt_sequence");
        builder.Property(entity => entity.Sequence).HasColumnName("sequence");
        builder.Property(entity => entity.JudgeRuntimeJson).HasColumnName("judge_runtime_json");
        builder.Property(entity => entity.JudgeExecutionKey).HasColumnName("judge_execution_key").HasMaxLength(64);
        builder.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.Verdict).HasColumnName("verdict").HasMaxLength(8);
        builder.Property(entity => entity.AnswerATruncated).HasColumnName("answer_a_truncated").HasDefaultValue(false);
        builder.Property(entity => entity.AnswerBTruncated).HasColumnName("answer_b_truncated").HasDefaultValue(false);
        builder.Property(entity => entity.ResultJson).HasColumnName("result_json");
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
        builder.HasOne<BenchmarkProject>().WithMany().HasForeignKey(entity => entity.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BenchmarkJudgePolicyRevision>().WithMany().HasForeignKey(entity => entity.PolicyRevisionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(entity => new
        {
            entity.ProjectId,
            entity.CohortGeneration
        }).HasDatabaseName("ix_benchmark_comparisons_project_generation");

        // The two slot-uniqueness indexes are DELIBERATELY absent here and written as raw SQL by the migration.
        // They index COALESCE(task_case_id, x'00'), and HasIndex() takes columns rather than expressions: declaring
        // them here would emit an index on the bare nullable column, which SQLite lets repeat NULLs — i.e. exactly
        // the uniqueness hole the COALESCE exists to close, reopened silently. See the migration's Sql() blocks.
    }
}
