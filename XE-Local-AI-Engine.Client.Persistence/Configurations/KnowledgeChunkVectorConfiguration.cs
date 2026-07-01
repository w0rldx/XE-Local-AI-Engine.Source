namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class KnowledgeChunkVectorConfiguration : IEntityTypeConfiguration<KnowledgeChunkVector>
{
    public void Configure(EntityTypeBuilder<KnowledgeChunkVector> builder)
    {
        // knowledge_ prefix: see KnowledgeDocumentConfiguration — avoids clashing with the shared node-chat schema.
        builder.ToTable("knowledge_chunk_vectors");
        builder.HasKey(entity => entity.ChunkId);

        builder.Property(entity => entity.ChunkId)
               .HasColumnName("chunk_id");

        builder.Property(entity => entity.DocumentId)
               .HasColumnName("document_id");

        builder.Property(entity => entity.Dim)
               .HasColumnName("dim");

        builder.Property(entity => entity.Embedding)
               .HasColumnName("embedding");

        builder.Property(entity => entity.EmbeddingModel)
               .HasColumnName("embedding_model");

        // Cascade from the owning chunk. The principal key is the chunk's UNIQUE alternate key (chunk_id), not its rowid
        // primary key. Documented FK intent only — the node-sqlite runtime connection does NOT enable PRAGMA foreign_keys,
        // so deletes are issued explicitly by the raw-SQL purge path (Lane D).
        builder.HasOne<KnowledgeDocumentChunk>()
               .WithOne()
               .HasForeignKey<KnowledgeChunkVector>(entity => entity.ChunkId)
               .HasPrincipalKey<KnowledgeDocumentChunk>(chunk => chunk.ChunkId)
               .OnDelete(DeleteBehavior.Cascade);

        // Cascade from the owning document too, so a document-scoped purge can drop its vectors directly.
        builder.HasOne<KnowledgeDocument>()
               .WithMany()
               .HasForeignKey(entity => entity.DocumentId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(entity => entity.DocumentId);
        builder.HasIndex(entity => entity.EmbeddingModel);
    }
}
