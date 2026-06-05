namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class NodeConversationConfiguration : IEntityTypeConfiguration<NodeConversation>
{
    public void Configure(EntityTypeBuilder<NodeConversation> builder)
    {
        builder.ToTable("conversations");
        builder.HasKey(entity => entity.ConversationId);

        builder.Property(entity => entity.ConversationId)
               .HasColumnName("conversation_id");

        builder.Property(entity => entity.Title)
               .HasColumnName("title");

        builder.Property(entity => entity.UserId)
               .HasColumnName("user_id");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.LastSeenUtc)
               .HasColumnName("last_seen_utc");

        builder.Property(entity => entity.Purged)
               .HasColumnName("purged");

        builder.Property(entity => entity.IsPinned)
               .HasColumnName("is_pinned")
               .HasDefaultValue(false);

        builder.Property(entity => entity.Archived)
               .HasColumnName("archived")
               .HasDefaultValue(false);

        builder.Property(entity => entity.Origin)
               .HasColumnName("origin")
               .HasDefaultValue(NodeChatOrigin.Local);

        builder.Property(entity => entity.BranchOfConversationId)
               .HasColumnName("branch_of_conversation_id");

        builder.Property(entity => entity.SelectedPathJson)
               .HasColumnName("selected_path_json");

        builder.Property(entity => entity.AgentDefinitionId)
               .HasColumnName("agent_definition_id");

        builder.HasMany(entity => entity.Messages)
               .WithOne(entity => entity.Conversation)
               .HasForeignKey(entity => entity.ConversationId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(entity => entity.ToolEvents)
               .WithOne(entity => entity.Conversation)
               .HasForeignKey(entity => entity.ConversationId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
