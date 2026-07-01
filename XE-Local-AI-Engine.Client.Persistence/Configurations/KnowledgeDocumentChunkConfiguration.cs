namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class KnowledgeDocumentChunkConfiguration : IEntityTypeConfiguration<KnowledgeDocumentChunk>
{
    public void Configure(EntityTypeBuilder<KnowledgeDocumentChunk> builder)
    {
        // knowledge_ prefix: see KnowledgeDocumentConfiguration — avoids clashing with the shared node-chat schema.
        builder.ToTable("knowledge_document_chunks");

        // KEY NUANCE: a SQLite table has exactly one primary key. The integer rowid alias is the key here because it is
        // stable across a database vacuum and is the row reference the chunk_fts external-content index aligns on. ChunkId
        // is a unique alternate key rather than a second primary key. The generated migration must keep rowid as a plain
        // integer rowid-alias primary key (a normal rowid table) so full-text external content stays valid.
        builder.HasKey(entity => entity.Rowid);

        builder.Property(entity => entity.Rowid)
               .HasColumnName("rowid")
               .ValueGeneratedOnAdd();

        builder.Property(entity => entity.ChunkId)
               .HasColumnName("chunk_id");

        builder.Property(entity => entity.DocumentId)
               .HasColumnName("document_id");

        builder.Property(entity => entity.SectionId)
               .HasColumnName("section_id");

        builder.Property(entity => entity.ChunkIndex)
               .HasColumnName("chunk_index");

        builder.Property(entity => entity.Content)
               .HasColumnName("content");

        builder.Property(entity => entity.TokenCount)
               .HasColumnName("token_count");

        builder.Property(entity => entity.HeadingPath)
               .HasColumnName("heading_path");

        // UNIQUE(chunk_id) as an alternate key so the vector index can foreign-key to the stable GUID rather than the rowid.
        builder.HasAlternateKey(entity => entity.ChunkId);

        // Cascade from the owning document; documented FK intent (runtime FK is OFF — see KnowledgeDocumentSectionConfiguration).
        builder.HasOne<KnowledgeDocument>()
               .WithMany()
               .HasForeignKey(entity => entity.DocumentId)
               .OnDelete(DeleteBehavior.Cascade);

        // Set-null from the owning section so orphaned chunks survive a section rebuild; documented FK intent.
        builder.HasOne<KnowledgeDocumentSection>()
               .WithMany()
               .HasForeignKey(entity => entity.SectionId)
               .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(entity => new { entity.DocumentId, entity.ChunkIndex });
        builder.HasIndex(entity => entity.SectionId);
    }
}
