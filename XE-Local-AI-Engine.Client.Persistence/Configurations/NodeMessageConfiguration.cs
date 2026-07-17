namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class NodeMessageConfiguration : IEntityTypeConfiguration<NodeMessage>
{
    public void Configure(EntityTypeBuilder<NodeMessage> builder)
    {
        builder.ToTable("messages");
        builder.HasKey(entity => entity.MessageId);

        builder.Property(entity => entity.MessageId)
               .HasColumnName("message_id");

        builder.Property(entity => entity.ConversationId)
               .HasColumnName("conversation_id");

        builder.Property(entity => entity.Sequence)
               .HasColumnName("sequence");

        builder.Property(entity => entity.Role)
               .HasColumnName("role");

        builder.Property(entity => entity.Content)
               .HasColumnName("content");

        builder.Property(entity => entity.MetadataJson)
               .HasColumnName("metadata_json");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.UpdatedAtUtc)
               .HasColumnName("updated_at_utc");

        builder.Property(entity => entity.Status)
               .HasColumnName("status")
               .HasDefaultValue(NodeMessageStatus.Completed);

        builder.Property(entity => entity.Origin)
               .HasColumnName("origin")
               .HasDefaultValue(NodeChatOrigin.Local);

        builder.Property(entity => entity.RequestId)
               .HasColumnName("request_id");

        builder.Property(entity => entity.Error)
               .HasColumnName("error");

        builder.Property(entity => entity.ParentMessageId)
               .HasColumnName("parent_message_id");

        builder.Property(entity => entity.VariantGroupId)
               .HasColumnName("variant_group_id");

        builder.Property(entity => entity.AgentDefinitionId)
               .HasColumnName("agent_definition_id");

        builder.HasIndex(entity => entity.RequestId);
        builder.HasIndex(entity => entity.ParentMessageId);
        builder.HasIndex(entity => entity.VariantGroupId);
        builder.HasIndex(entity => entity.AgentDefinitionId);

        // A conversation's message sequences must be unique: two concurrent sends/regenerations once allocated the same
        // MAX(sequence)+1 and both inserted, corrupting ordering. Allocation is now serialized under a per-conversation
        // write lock, and this index is the database-level backstop that turns any residual collision into a retryable
        // constraint error instead of a silent duplicate.
        builder.HasIndex(entity => new
               {
                   entity.ConversationId,
                   entity.Sequence
               })
               .IsUnique();
    }
}
