namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class NodePurgedTombstoneConfiguration : IEntityTypeConfiguration<NodePurgedTombstone>
{
    public void Configure(EntityTypeBuilder<NodePurgedTombstone> builder)
    {
        builder.ToTable("purged_tombstones");
        builder.HasKey(entity => entity.ConversationId);

        builder.Property(entity => entity.ConversationId)
               .HasColumnName("conversation_id");

        builder.Property(entity => entity.PurgedAtUtc)
               .HasColumnName("purged_at_utc");

        builder.Property(entity => entity.AckedAtUtc)
               .HasColumnName("acked_at_utc");
    }
}
