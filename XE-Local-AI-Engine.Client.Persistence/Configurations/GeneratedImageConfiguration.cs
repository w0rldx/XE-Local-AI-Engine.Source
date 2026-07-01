namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class GeneratedImageConfiguration : IEntityTypeConfiguration<GeneratedImage>
{
    public void Configure(EntityTypeBuilder<GeneratedImage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("generated_images");
        builder.HasKey(entity => entity.ImageId);

        builder.Property(entity => entity.ImageId).HasColumnName("image_id");
        builder.Property(entity => entity.JobId).HasColumnName("job_id");
        builder.Property(entity => entity.MimeType).HasColumnName("mime_type");
        builder.Property(entity => entity.Width).HasColumnName("width");
        builder.Property(entity => entity.Height).HasColumnName("height");
        builder.Property(entity => entity.SizeBytes).HasColumnName("size_bytes");
        builder.Property(entity => entity.StoragePath).HasColumnName("storage_path");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");

        // Cascade from the owning image job. The FK is configured without a navigation on the principal so the ImageJob
        // entity stays untouched — same posture as ConversationUploadedFile's conversation FK.
        builder.HasOne<ImageJob>()
               .WithMany()
               .HasForeignKey(entity => entity.JobId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(entity => entity.JobId);
    }
}
