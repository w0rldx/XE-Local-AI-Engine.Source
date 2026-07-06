namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class KnowledgeDocumentSectionConfiguration : IEntityTypeConfiguration<KnowledgeDocumentSection>
{
    public void Configure(EntityTypeBuilder<KnowledgeDocumentSection> builder)
    {
        // knowledge_ prefix: see KnowledgeDocumentConfiguration — avoids clashing with the shared node-chat schema.
        builder.ToTable("knowledge_document_sections");
        builder.HasKey(entity => entity.SectionId);

        builder.Property(entity => entity.SectionId)
               .HasColumnName("section_id");

        builder.Property(entity => entity.DocumentId)
               .HasColumnName("document_id");

        builder.Property(entity => entity.Ordinal)
               .HasColumnName("ordinal");

        builder.Property(entity => entity.Heading)
               .HasColumnName("heading");

        builder.Property(entity => entity.Level)
               .HasColumnName("level");

        // Cascade from the owning document. Configured without a navigation on the principal so KnowledgeDocument stays a
        // bare metadata row. The node-sqlite runtime connection does NOT enable PRAGMA foreign_keys, so this cascade
        // documents intent and serves EF-managed deletes (tests); the raw-SQL purge path (Lane D) removes rows explicitly.
        builder.HasOne<KnowledgeDocument>()
               .WithMany()
               .HasForeignKey(entity => entity.DocumentId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(entity => new
        {
            entity.DocumentId,
            entity.Ordinal
        });
    }
}
