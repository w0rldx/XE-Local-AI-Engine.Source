namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class ConversationUploadedFileConfiguration : IEntityTypeConfiguration<ConversationUploadedFile>
{
    public void Configure(EntityTypeBuilder<ConversationUploadedFile> builder)
    {
        builder.ToTable("conversation_uploaded_files");
        builder.HasKey(entity => entity.FileId);

        builder.Property(entity => entity.FileId)
               .HasColumnName("file_id");

        builder.Property(entity => entity.ConversationId)
               .HasColumnName("conversation_id");

        builder.Property(entity => entity.OriginalFileName)
               .HasColumnName("original_file_name");

        builder.Property(entity => entity.MimeType)
               .HasColumnName("mime_type");

        builder.Property(entity => entity.Extension)
               .HasColumnName("extension");

        builder.Property(entity => entity.SizeBytes)
               .HasColumnName("size_bytes");

        builder.Property(entity => entity.ExtractionStatus)
               .HasColumnName("extraction_status");

        builder.Property(entity => entity.ExtractedChars)
               .HasColumnName("extracted_chars");

        builder.Property(entity => entity.StoragePath)
               .HasColumnName("storage_path");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        // Cascade from the owning conversation. The FK is configured without a navigation on the principal so the
        // NodeConversation entity stays untouched. The node-sqlite runtime connection does NOT enable
        // PRAGMA foreign_keys, so this cascade documents intent and serves EF-managed deletes (tests); the raw-SQL
        // purge path removes the rows explicitly. Disk-resident bytes/extracted text are cleaned by the file store.
        builder.HasOne<NodeConversation>()
               .WithMany()
               .HasForeignKey(entity => entity.ConversationId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(entity => entity.ConversationId);
    }
}
