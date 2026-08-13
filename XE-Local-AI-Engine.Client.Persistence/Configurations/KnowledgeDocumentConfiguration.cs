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

        builder.Property(entity => entity.CollectionId)
               .HasColumnName("collection_id")
               .HasDefaultValue("DEFAULT");

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

        builder.Property(entity => entity.SourcePath)
               .HasColumnName("source_path");

        builder.Property(entity => entity.SourceKind)
               .HasColumnName("source_kind")
               .HasDefaultValue("upload");

        builder.Property(entity => entity.SourceId)
               .HasColumnName("source_id");

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

        builder.Property(entity => entity.ParserVersion)
               .HasColumnName("parser_version")
               .HasDefaultValue("legacy");

        builder.Property(entity => entity.ChunkerVersion)
               .HasColumnName("chunker_version")
               .HasDefaultValue("legacy");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.UpdatedAtUtc)
               .HasColumnName("updated_at_utc");

        // Ordinary uploads dedupe by collection + content hash. Repository documents deliberately do not: identical
        // bytes at two paths are two sources, while collection + source kind + source id + path gives a changed file
        // stable identity without allowing two repositories in one collection to overwrite each other.
        builder.HasIndex(entity => new
               {
                   entity.CollectionId,
                   entity.ContentHash
               })
               .IsUnique()
               .HasFilter("source_kind <> 'repository'");

        builder.HasIndex(entity => new
               {
                   entity.CollectionId,
                   entity.SourceKind,
                   entity.SourceId,
                   entity.SourcePath
               })
               .IsUnique()
               .HasFilter("source_kind = 'repository' AND source_id IS NOT NULL AND source_path IS NOT NULL");

        builder.HasIndex(entity => entity.Status);
        builder.HasIndex(entity => new
        {
            entity.CollectionId,
            entity.Status
        });
    }
}
