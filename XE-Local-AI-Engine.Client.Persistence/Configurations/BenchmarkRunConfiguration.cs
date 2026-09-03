namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

internal sealed class BenchmarkRunConfiguration : IEntityTypeConfiguration<BenchmarkRun>
{
    public void Configure(EntityTypeBuilder<BenchmarkRun> builder)
    {
        builder.ToTable("benchmark_runs", table =>
        {
            table.HasCheckConstraint("CK_benchmark_runs_user_score", "user_score IS NULL OR (user_score >= 0 AND user_score <= 100)");
            table.HasCheckConstraint("CK_benchmark_runs_model_origin", "primary_model_origin IS NULL OR primary_model_origin IN ('huggingface', 'imported', 'trained')");
            table.HasCheckConstraint("CK_benchmark_runs_requested_context", "requested_context_tokens > 0");
        });
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.ProjectId).HasColumnName("project_id");
        builder.Property(entity => entity.RuntimeSnapshotJson).HasColumnName("runtime_snapshot_json").IsRequired();
        builder.Property(entity => entity.PrimaryModelName).HasColumnName("primary_model_name").HasMaxLength(255).UseCollation("NOCASE").IsRequired();
        builder.Property(entity => entity.PrimaryModelOrigin)
               .HasColumnName("primary_model_origin")
               .HasConversion(static value => ConvertOriginToStore(value),
                   static value => ConvertOriginFromStore(value));
        builder.Property(entity => entity.ModelContentFingerprint).HasColumnName("model_content_fingerprint").HasMaxLength(67).IsRequired();
        builder.Property(entity => entity.AgentName).HasColumnName("agent_name").HasMaxLength(200).IsRequired();
        builder.Property(entity => entity.AgentVersion).HasColumnName("agent_version");
        builder.Property(entity => entity.RequestedContextTokens).HasColumnName("requested_context_tokens");
        builder.Property(entity => entity.InvocationTimeoutSeconds).HasColumnName("invocation_timeout_seconds");
        builder.Property(entity => entity.PrimaryStatus).HasColumnName("primary_status").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.EffectiveContextTokens).HasColumnName("effective_context_tokens");
        builder.Property(entity => entity.DurationMs).HasColumnName("duration_ms");
        builder.Property(entity => entity.TotalTokens).HasColumnName("total_tokens");
        builder.Property(entity => entity.TokensPerSecond).HasColumnName("tokens_per_second");

        // Plaintext numerics, same posture as tokens_per_second: no secrets, no content, nothing node-scoped.
        builder.Property(entity => entity.TtftMs).HasColumnName("ttft_ms");
        builder.Property(entity => entity.PromptTokens).HasColumnName("prompt_tokens");
        builder.Property(entity => entity.PromptMs).HasColumnName("prompt_ms");
        builder.Property(entity => entity.GenerationTokens).HasColumnName("generation_tokens");
        builder.Property(entity => entity.GenerationMs).HasColumnName("generation_ms");
        builder.Property(entity => entity.CachedPromptTokens).HasColumnName("cached_prompt_tokens");
        builder.Property(entity => entity.SegmentCount).HasColumnName("segment_count");
        builder.Property(entity => entity.OutputPartsJson).HasColumnName("output_parts_json");
        builder.Property(entity => entity.LastStreamSequence).HasColumnName("last_stream_sequence");
        builder.Property(entity => entity.UserScore).HasColumnName("user_score");
        builder.Property(entity => entity.RepeatGroupId).HasColumnName("repeat_group_id");
        builder.Property(entity => entity.RepeatIndex).HasColumnName("repeat_index");
        builder.Property(entity => entity.IsWarmup).HasColumnName("is_warmup").HasDefaultValue(false);
        builder.Property(entity => entity.RepeatMode)
               .HasColumnName("repeat_mode")
               .HasConversion<string>()
               .HasMaxLength(32)
               .HasDefaultValue(BenchmarkRepeatMode.Throughput);
        builder.Property(entity => entity.SamplingSeed).HasColumnName("sampling_seed").HasMaxLength(32);
        builder.Property(entity => entity.SamplingTemperature).HasColumnName("sampling_temperature");
        // The four immutable identity stamps. Plaintext, like every other column the ranking read scans; cell_key and
        // both hashes are NOT NULL so a missing stamp can never be read as "belongs with everything else".
        builder.Property(entity => entity.TaskItemId).HasColumnName("task_item_id");
        builder.Property(entity => entity.TaskItemIndex).HasColumnName("task_item_index");
        builder.Property(entity => entity.CellKey).HasColumnName("cell_key").HasMaxLength(64).IsRequired();
        // The two hash stamps default to the legacy constant so the migration can make them NOT NULL over existing
        // rows: a run frozen before task items existed is compared against the same constant on both axes and is
        // therefore never read as stale. Every insert writes its own value, so the default only ever describes history.
        builder.Property(entity => entity.TaskInputHash)
               .HasColumnName("task_input_hash")
               .HasMaxLength(67)
               .HasDefaultValue(BenchmarkStore.LegacyTaskHash)
               .IsRequired();
        builder.Property(entity => entity.TaskItemSetHash)
               .HasColumnName("task_item_set_hash")
               .HasMaxLength(67)
               .HasDefaultValue(BenchmarkStore.LegacyTaskHash)
               .IsRequired();
        builder.Property(entity => entity.CurrentJudgeAttemptId).HasColumnName("current_judge_attempt_id");

        // The fidelity projection: plaintext numerics, same posture as tokens_per_second above. Display only —
        // ranking never reads them. The encrypted fidelity receipt lives on the attempt row, not here.
        builder.Property(entity => entity.FidelityAttemptId).HasColumnName("fidelity_attempt_id");
        builder.Property(entity => entity.PerplexityMean).HasColumnName("perplexity_mean");
        builder.Property(entity => entity.PerplexityStdErr).HasColumnName("perplexity_std_err");
        builder.Property(entity => entity.PerplexityChunks).HasColumnName("perplexity_chunks");
        builder.Property(entity => entity.PerplexityContextTokens).HasColumnName("perplexity_context_tokens");
        builder.Property(entity => entity.PerplexityCorpusId).HasColumnName("perplexity_corpus_id").HasMaxLength(64);
        builder.Property(entity => entity.KldMean).HasColumnName("kld_mean");
        builder.Property(entity => entity.KldP99).HasColumnName("kld_p99");
        builder.Property(entity => entity.TopTokenAgreement).HasColumnName("top_token_agreement");
        builder.Property(entity => entity.KldBaseFingerprint).HasColumnName("kld_base_fingerprint").HasMaxLength(67);
        builder.Property(entity => entity.KldBaseLogitsDigest).HasColumnName("kld_base_logits_digest").HasMaxLength(67);
        builder.Property(entity => entity.FidelityStatus).HasColumnName("fidelity_status").HasMaxLength(16);
        builder.Property(entity => entity.FidelityErrorMessage).HasColumnName("fidelity_error_message").HasMaxLength(1024);
        builder.Property(entity => entity.PrimaryVariant).HasColumnName("primary_variant").HasMaxLength(32);
        builder.Property(entity => entity.PrimaryKvCacheType).HasColumnName("primary_kv_cache_type").HasMaxLength(32);
        builder.Property(entity => entity.PrimaryKvCacheTypeSource).HasColumnName("primary_kv_cache_type_source").HasMaxLength(16);
        builder.Property(entity => entity.PrimaryKvAutoReason).HasColumnName("primary_kv_auto_reason").HasMaxLength(64);
        builder.Property(entity => entity.PrimaryFlashAttentionMode).HasColumnName("primary_flash_attention_mode").HasMaxLength(16);
        builder.Property(entity => entity.PrimaryIntendedLaunchIdentity).HasColumnName("primary_intended_launch_identity").HasMaxLength(64);
        builder.Property(entity => entity.PrimaryLaunchIdentityScheme).HasColumnName("primary_launch_identity_scheme");
        builder.Property(entity => entity.PrimaryIntendedExecutableSha256).HasColumnName("primary_intended_executable_sha256").HasMaxLength(64);
        builder.Property(entity => entity.PrimaryLaunchReceiptJson).HasColumnName("primary_launch_receipt_json");
        builder.Property(entity => entity.PrimaryEnvironmentFactsJson).HasColumnName("primary_environment_facts_json");
        builder.Property(entity => entity.PrimaryReceiptHash).HasColumnName("primary_receipt_hash").HasMaxLength(64);
        builder.Property(entity => entity.PrimaryEnvironmentFactsHash).HasColumnName("primary_environment_facts_hash").HasMaxLength(64);
        builder.Property(entity => entity.PrimaryEffectiveLaunchIdentity).HasColumnName("primary_effective_launch_identity").HasMaxLength(64);
        builder.Property(entity => entity.PrimaryEffectiveBackend).HasColumnName("primary_effective_backend").HasMaxLength(32);
        builder.Property(entity => entity.PrimaryPlacementOffloaded).HasColumnName("primary_placement_offloaded");
        builder.Property(entity => entity.PrimaryPlacementTotal).HasColumnName("primary_placement_total");
        builder.Property(entity => entity.PrimaryLaunchExecutableSha256).HasColumnName("primary_launch_executable_sha256").HasMaxLength(64);
        builder.Property(entity => entity.PrimaryLaunchHasAuxAssets).HasColumnName("primary_launch_has_aux_assets");
        builder.Property(entity => entity.PrimaryLaunchKvCacheTypeSource).HasColumnName("primary_launch_kv_cache_type_source").HasMaxLength(16);
        builder.Property(entity => entity.PrimaryStopReason).HasColumnName("primary_stop_reason").HasMaxLength(32);
        builder.Property(entity => entity.PrimaryErrorMessage).HasColumnName("primary_error_message").HasMaxLength(1024);
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.StartedAtUtc).HasColumnName("started_at_utc");
        builder.Property(entity => entity.PrimaryCompletedAtUtc).HasColumnName("primary_completed_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.HasOne<BenchmarkProject>().WithMany().HasForeignKey(entity => entity.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(entity => new
        {
            entity.ProjectId,
            entity.CreatedAtUtc
        }).HasDatabaseName("ix_benchmark_runs_project_created_at");
        builder.HasIndex(entity => new
        {
            entity.ProjectId,
            entity.PrimaryKvCacheType
        }).HasDatabaseName("ix_benchmark_runs_project_primary_kv_cache_type");
        builder.HasIndex(entity => entity.RepeatGroupId).HasDatabaseName("ix_benchmark_runs_repeat_group_id");
        builder.HasIndex(entity => new
        {
            entity.ProjectId,
            entity.TaskItemId
        }).HasDatabaseName("ix_benchmark_runs_project_task_item_id");
        builder.HasIndex(entity => new
        {
            entity.ProjectId,
            entity.CellKey
        }).HasDatabaseName("ix_benchmark_runs_project_cell_key");
    }

    private static string? ConvertOriginToStore(LocalModelOrigin? value) =>
        value switch
        {
            null => null,
            LocalModelOrigin.HuggingFace => "huggingface",
            LocalModelOrigin.Imported => "imported",
            LocalModelOrigin.Trained => "trained",
            _ => throw new InvalidOperationException("Unknown benchmark model origin enum value.")
        };

    private static LocalModelOrigin? ConvertOriginFromStore(string? value) =>
        value switch
        {
            null => null,
            "huggingface" => LocalModelOrigin.HuggingFace,
            "imported" => LocalModelOrigin.Imported,
            "trained" => LocalModelOrigin.Trained,
            _ => throw new InvalidOperationException("Unknown benchmark model origin database value.")
        };
}
