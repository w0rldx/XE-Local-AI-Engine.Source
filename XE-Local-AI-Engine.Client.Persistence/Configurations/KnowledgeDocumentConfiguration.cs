namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class KnowledgeDocumentConfiguration : IEntityTypeConfiguration<KnowledgeDocument>
{
    public void Configure(EntityTypeBuilder<KnowledgeDocument> builder)
    {
        // Prefixed with knowledge_ to avoid colliding with the node-chat `documents` table — this table lives in the
        // same node-chat database as the conversation tables.
        builder.ToTable("knowledge_documents");
        builder.HasKey(entity => entity.DocumentId);

        builder.Property(entity => entity.DocumentId)
               .HasColumnName("document_id");

        builder.Property(entity => entity.OriginalFileName)
               .HasColumnName("original_file_name");

        builder.Property(entity => entity.MimeType)
               .HasColumnName("mime_type");

        builder.Property(entity => entity.Extension)
               .HasColumnName("extension");

        builder.Property(entity => entity.SizeBytes)
               .HasColumnName("size_bytes");

        builder.Property(entity => entity.ContentHash)
               .HasColumnName("content_hash");

        builder.Property(entity => entity.StoragePath)
               .HasColumnName("storage_path");

        builder.Property(entity => entity.Status)
               .HasColumnName("status");

        builder.Property(entity => entity.FailureReason)
               .HasColumnName("failure_reason");

        builder.Property(entity => entity.ChunkCount)
               .HasColumnName("chunk_count");

        builder.Property(entity => entity.EmbeddingModel)
               .HasColumnName("embedding_model");

        builder.Property(entity => entity.VectorIdentity)
               .HasColumnName("vector_identity")
               .HasDefaultValue("legacy:unversioned");

        builder.Property(entity => entity.VectorDim)
               .HasColumnName("vector_dim")
               .HasDefaultValue(0);

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.UpdatedAtUtc)
               .HasColumnName("updated_at_utc");

        // UNIQUE content hash so a duplicate upload is deduped (store uses INSERT ... ON CONFLICT DO NOTHING, not
        // check-then-insert), plus a status index for the management/list queries.
        builder.HasIndex(entity => entity.ContentHash)
               .IsUnique();

        builder.HasIndex(entity => entity.Status);
    }
}
