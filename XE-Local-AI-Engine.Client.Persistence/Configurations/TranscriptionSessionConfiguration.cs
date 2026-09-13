namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class TranscriptionSessionConfiguration : IEntityTypeConfiguration<TranscriptionSession>
{
    public void Configure(EntityTypeBuilder<TranscriptionSession> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("transcription_sessions");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.Title).HasColumnName("title");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.Property(entity => entity.Status).HasColumnName("status");
        builder.Property(entity => entity.SourceKind).HasColumnName("source_kind");
        builder.Property(entity => entity.ModelId).HasColumnName("model_id").HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.ConfigJson).HasColumnName("config_json");
        builder.Property(entity => entity.DetectedLanguage).HasColumnName("detected_language").HasMaxLength(16);
        builder.Property(entity => entity.DurationMs).HasColumnName("duration_ms");
        builder.Property(entity => entity.ErrorCode).HasColumnName("error_code");
        builder.Property(entity => entity.ErrorMessage).HasColumnName("error_message");

        builder.HasIndex(entity => entity.CreatedAtUtc);
        builder.HasIndex(entity => entity.Status);
    }
}
