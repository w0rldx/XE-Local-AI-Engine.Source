namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class NodeMessageFeedbackConfiguration : IEntityTypeConfiguration<NodeMessageFeedback>
{
    public void Configure(EntityTypeBuilder<NodeMessageFeedback> builder)
    {
        builder.ToTable("message_feedback");
        builder.HasKey(entity => entity.MessageId);

        builder.Property(entity => entity.MessageId)
               .HasColumnName("message_id");

        builder.Property(entity => entity.ConversationId)
               .HasColumnName("conversation_id");

        builder.Property(entity => entity.Rating)
               .HasColumnName("rating");

        builder.Property(entity => entity.Comment)
               .HasColumnName("comment");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.UpdatedAtUtc)
               .HasColumnName("updated_at_utc");

        builder.HasOne(entity => entity.Message)
               .WithOne()
               .HasForeignKey<NodeMessageFeedback>(entity => entity.MessageId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(entity => entity.ConversationId);
    }
}
