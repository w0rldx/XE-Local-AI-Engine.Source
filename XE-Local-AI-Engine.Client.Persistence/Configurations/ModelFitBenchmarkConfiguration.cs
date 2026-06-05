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

        builder.HasIndex(entity => entity.SnapshotId);

        // A benchmark row is meaningless without its parent snapshot, so the FK cascades: deleting a snapshot removes
        // its benchmark rows.
        builder.HasOne<ModelFitSnapshot>()
               .WithMany()
               .HasForeignKey(entity => entity.SnapshotId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
