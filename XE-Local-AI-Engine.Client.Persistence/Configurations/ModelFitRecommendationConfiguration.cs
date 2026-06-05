namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class ModelFitRecommendationConfiguration : IEntityTypeConfiguration<ModelFitRecommendation>
{
    public void Configure(EntityTypeBuilder<ModelFitRecommendation> builder)
    {
        builder.ToTable("model_fit_recommendations");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.SnapshotId)
               .HasColumnName("snapshot_id");

        builder.Property(entity => entity.Rank)
               .HasColumnName("rank");

        builder.Property(entity => entity.ModelName)
               .HasColumnName("model_name");

        builder.Property(entity => entity.ProviderModelName)
               .HasColumnName("provider_model_name");

        builder.Property(entity => entity.Score)
               .HasColumnName("score");

        builder.Property(entity => entity.FitLevel)
               .HasColumnName("fit_level");

        builder.Property(entity => entity.RunMode)
               .HasColumnName("run_mode");

        builder.Property(entity => entity.Quantization)
               .HasColumnName("quantization");

        builder.Property(entity => entity.EstimatedTokensPerSecond)
               .HasColumnName("estimated_tokens_per_second");

        builder.Property(entity => entity.RequiredRamMb)
               .HasColumnName("required_ram_mb");

        builder.Property(entity => entity.RequiredVramMb)
               .HasColumnName("required_vram_mb");

        builder.Property(entity => entity.ContextTokens)
               .HasColumnName("context_tokens");

        builder.Property(entity => entity.IsInstalled)
               .HasColumnName("is_installed");

        builder.Property(entity => entity.PullModelName)
               .HasColumnName("pull_model_name");

        builder.Property(entity => entity.DiagnosticsJson)
               .HasColumnName("diagnostics_json");

        builder.HasIndex(entity => new { entity.SnapshotId, entity.Rank });

        // A recommendation row is meaningless without its parent snapshot, so the FK cascades: deleting a snapshot
        // removes its recommendation rows.
        builder.HasOne<ModelFitSnapshot>()
               .WithMany()
               .HasForeignKey(entity => entity.SnapshotId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
