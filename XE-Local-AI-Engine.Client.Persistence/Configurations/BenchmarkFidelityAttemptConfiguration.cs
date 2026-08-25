namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class BenchmarkFidelityAttemptConfiguration : IEntityTypeConfiguration<BenchmarkFidelityAttempt>
{
    public void Configure(EntityTypeBuilder<BenchmarkFidelityAttempt> builder)
    {
        builder.ToTable("benchmark_fidelity_attempts", table =>
        {
            table.HasCheckConstraint("CK_benchmark_fidelity_attempts_sequence", "sequence > 0");
            table.HasCheckConstraint("CK_benchmark_fidelity_attempts_kind", "kind IN ('ppl', 'kld')");
            table.HasCheckConstraint("CK_benchmark_fidelity_attempts_status",
                "status IN ('Queued', 'Running', 'Succeeded', 'Failed', 'Cancelled')");
        });
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.RunId).HasColumnName("run_id");
        builder.Property(entity => entity.Sequence).HasColumnName("sequence");
        builder.Property(entity => entity.Kind).HasColumnName("kind").HasMaxLength(8).IsRequired();
        builder.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);

        // Plaintext numerics, same posture as benchmark_runs.tokens_per_second: no secrets, no content, nothing
        // node-scoped. Only the receipt below carries host paths and is therefore encrypted.
        builder.Property(entity => entity.PerplexityMean).HasColumnName("perplexity_mean");
        builder.Property(entity => entity.PerplexityStdErr).HasColumnName("perplexity_std_err");
        builder.Property(entity => entity.PerplexityChunks).HasColumnName("perplexity_chunks");
        builder.Property(entity => entity.PerplexityContextTokens).HasColumnName("perplexity_context_tokens");
        builder.Property(entity => entity.CorpusId).HasColumnName("corpus_id").HasMaxLength(64);
        builder.Property(entity => entity.KldMean).HasColumnName("kld_mean");
        builder.Property(entity => entity.KldP99).HasColumnName("kld_p99");
        builder.Property(entity => entity.TopTokenAgreement).HasColumnName("top_token_agreement");
        builder.Property(entity => entity.BaseModelName).HasColumnName("base_model_name").HasMaxLength(256);
        builder.Property(entity => entity.BaseModelContentFingerprint).HasColumnName("base_model_content_fingerprint").HasMaxLength(67);
        builder.Property(entity => entity.BaseLogitsDigest).HasColumnName("base_logits_digest").HasMaxLength(67);
        builder.Property(entity => entity.ReceiptJson).HasColumnName("receipt_json");
        builder.Property(entity => entity.ErrorMessage).HasColumnName("error_message").HasMaxLength(1024);
        builder.Property(entity => entity.EnqueuedAtUtc).HasColumnName("enqueued_at_utc");
        builder.Property(entity => entity.StartedAtUtc).HasColumnName("started_at_utc");
        builder.Property(entity => entity.CompletedAtUtc).HasColumnName("completed_at_utc");
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.HasOne<BenchmarkRun>().WithMany().HasForeignKey(entity => entity.RunId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(entity => new
        {
            entity.RunId,
            entity.Sequence
        }).IsUnique().HasDatabaseName("ux_benchmark_fidelity_attempts_run_sequence");
        builder.HasIndex(entity => new
        {
            entity.RunId,
            entity.Status
        }).HasDatabaseName("ix_benchmark_fidelity_attempts_run_status");
    }
}
