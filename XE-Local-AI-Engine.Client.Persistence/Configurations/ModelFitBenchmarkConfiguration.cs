namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class ModelFitBenchmarkConfiguration : IEntityTypeConfiguration<ModelFitBenchmark>
{
    public void Configure(EntityTypeBuilder<ModelFitBenchmark> builder)
    {
        builder.ToTable("model_fit_benchmarks");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.SnapshotId)
               .HasColumnName("snapshot_id");

        builder.Property(entity => entity.ModelName)
               .HasColumnName("model_name");

        builder.Property(entity => entity.ProviderName)
               .HasColumnName("provider_name");

        builder.Property(entity => entity.TokensPerSecond)
               .HasColumnName("tokens_per_second");

        builder.Property(entity => entity.TtftMs)
               .HasColumnName("ttft_ms");

        builder.Property(entity => entity.TotalLatencyMs)
               .HasColumnName("total_latency_ms");

        builder.Property(entity => entity.Runs)
               .HasColumnName("runs");

        builder.Property(entity => entity.RawJson)
               .HasColumnName("raw_json");

        builder.Property(entity => entity.DiagnosticsJson)
               .HasColumnName("diagnostics_json");

        // Agent-loop metrics (additive, nullable, plaintext numerics — same posture as tokens_per_second).
        builder.Property(entity => entity.PpTokensPerSecond)
               .HasColumnName("pp_tokens_per_second");

        builder.Property(entity => entity.CacheHitRate)
               .HasColumnName("cache_hit_rate");

        builder.Property(entity => entity.ToolLoopMs)
               .HasColumnName("tool_loop_ms");

        builder.Property(entity => entity.VramLoadBytes)
               .HasColumnName("vram_load_bytes");

        builder.Property(entity => entity.VramAfterBytes)
               .HasColumnName("vram_after_bytes");

        // Reproducibility key.
        builder.Property(entity => entity.LlamacppBuild)
               .HasColumnName("llamacpp_build");

        builder.Property(entity => entity.Quant)
               .HasColumnName("quant");

        builder.Property(entity => entity.CtxSize)
               .HasColumnName("ctx_size");

        builder.Property(entity => entity.KvType)
               .HasColumnName("kv_type");

        builder.Property(entity => entity.Backend)
               .HasColumnName("backend");

        builder.Property(entity => entity.MachineKey)
               .HasColumnName("machine_key");

        // Placement args that dominate MoE tok/s.
        builder.Property(entity => entity.NGpuLayers)
               .HasColumnName("n_gpu_layers");

        builder.Property(entity => entity.TensorSplit)
               .HasColumnName("tensor_split");

        builder.Property(entity => entity.OverrideTensor)
               .HasColumnName("override_tensor");

        builder.HasIndex(entity => entity.SnapshotId);

        // A benchmark row is meaningless without its parent snapshot, so the FK cascades: deleting a snapshot removes
        // its benchmark rows.
        builder.HasOne<ModelFitSnapshot>()
               .WithMany()
               .HasForeignKey(entity => entity.SnapshotId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
