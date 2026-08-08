namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class ImageJobConfiguration : IEntityTypeConfiguration<ImageJob>
{
    public void Configure(EntityTypeBuilder<ImageJob> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("image_jobs");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.ModelName).HasColumnName("model_name");
        builder.Property(entity => entity.Prompt).HasColumnName("prompt");
        builder.Property(entity => entity.NegativePrompt).HasColumnName("negative_prompt");
        builder.Property(entity => entity.Seed).HasColumnName("seed");
        builder.Property(entity => entity.Width).HasColumnName("width");
        builder.Property(entity => entity.Height).HasColumnName("height");
        builder.Property(entity => entity.Steps).HasColumnName("steps");
        builder.Property(entity => entity.Sampler).HasColumnName("sampler");
        builder.Property(entity => entity.CfgScale).HasColumnName("cfg_scale");
        builder.Property(entity => entity.Status).HasColumnName("status");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.StartedAtUtc).HasColumnName("started_at_utc");
        builder.Property(entity => entity.CompletedAtUtc).HasColumnName("completed_at_utc");
        builder.Property(entity => entity.DurationMs).HasColumnName("duration_ms");
        builder.Property(entity => entity.ImageId).HasColumnName("image_id");
        builder.Property(entity => entity.SanitizedError).HasColumnName("sanitized_error");
        builder.Property(entity => entity.CancellationRequestedAtUtc).HasColumnName("cancellation_requested_at_utc");

        builder.HasIndex(entity => entity.Status);
        builder.HasIndex(entity => entity.CreatedAtUtc);
    }
}
