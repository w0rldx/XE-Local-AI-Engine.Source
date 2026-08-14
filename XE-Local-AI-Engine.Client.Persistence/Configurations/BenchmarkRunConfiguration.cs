namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

internal sealed class BenchmarkRunConfiguration : IEntityTypeConfiguration<BenchmarkRun>
{
    public void Configure(EntityTypeBuilder<BenchmarkRun> builder)
    {
        builder.ToTable("benchmark_runs", table =>
        {
            table.HasCheckConstraint("CK_benchmark_runs_user_score", "user_score IS NULL OR (user_score >= 1 AND user_score <= 5)");
            table.HasCheckConstraint("CK_benchmark_runs_model_origin", "primary_model_origin IS NULL OR primary_model_origin IN ('huggingface', 'imported')");
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
        builder.Property(entity => entity.PrimaryStatus).HasColumnName("primary_status").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.EffectiveContextTokens).HasColumnName("effective_context_tokens");
        builder.Property(entity => entity.DurationMs).HasColumnName("duration_ms");
        builder.Property(entity => entity.TotalTokens).HasColumnName("total_tokens");
        builder.Property(entity => entity.TokensPerSecond).HasColumnName("tokens_per_second");
        builder.Property(entity => entity.OutputPartsJson).HasColumnName("output_parts_json");
        builder.Property(entity => entity.LastStreamSequence).HasColumnName("last_stream_sequence");
        builder.Property(entity => entity.UserScore).HasColumnName("user_score");
        builder.Property(entity => entity.JudgeStatus).HasColumnName("judge_status").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.JudgeResultJson).HasColumnName("judge_result_json");
        builder.Property(entity => entity.PrimaryErrorMessage).HasColumnName("primary_error_message").HasMaxLength(1024);
        builder.Property(entity => entity.JudgeErrorMessage).HasColumnName("judge_error_message").HasMaxLength(1024);
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.StartedAtUtc).HasColumnName("started_at_utc");
        builder.Property(entity => entity.PrimaryCompletedAtUtc).HasColumnName("primary_completed_at_utc");
        builder.Property(entity => entity.JudgeStartedAtUtc).HasColumnName("judge_started_at_utc");
        builder.Property(entity => entity.JudgeCompletedAtUtc).HasColumnName("judge_completed_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.HasOne<BenchmarkProject>().WithMany().HasForeignKey(entity => entity.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(entity => new
        {
            entity.ProjectId,
            entity.CreatedAtUtc
        }).HasDatabaseName("ix_benchmark_runs_project_created_at");
    }

    private static string? ConvertOriginToStore(LocalModelOrigin? value) =>
        value switch
        {
            null => null,
            LocalModelOrigin.HuggingFace => "huggingface",
            LocalModelOrigin.Imported => "imported",
            _ => throw new InvalidOperationException("Unknown benchmark model origin enum value.")
        };

    private static LocalModelOrigin? ConvertOriginFromStore(string? value) =>
        value switch
        {
            null => null,
            "huggingface" => LocalModelOrigin.HuggingFace,
            "imported" => LocalModelOrigin.Imported,
            _ => throw new InvalidOperationException("Unknown benchmark model origin database value.")
        };
}
