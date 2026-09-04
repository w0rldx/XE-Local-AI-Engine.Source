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

        // The chat/work-session/integration discriminator. Additive with a default, so a pre-feature
        // conversation reads as an ordinary chat and an older binary's inserts still succeed.
        builder.Property(entity => entity.Kind)
               .HasColumnName("kind")
               .HasMaxLength(32)
               .HasDefaultValue(NodeConversationKind.Chat);

        builder.Property(entity => entity.BranchOfConversationId)
               .HasColumnName("branch_of_conversation_id");

        builder.Property(entity => entity.SelectedPathJson)
               .HasColumnName("selected_path_json");

        builder.Property(entity => entity.AgentDefinitionId)
               .HasColumnName("agent_definition_id");

        // Temporary-chat flag that suppresses post-run adaptive-memory extraction — additive structural column.
        // Plaintext (a bool); default and backfill false so a pre-feature conversation reads as non-temporary.
        builder.Property(entity => entity.MemoryExcluded)
               .HasColumnName("memory_excluded")
               .HasDefaultValue(false);

        // Non-destructive compaction synopsis (encrypted BLOB) + the highest message sequence it folds in + its
        // last-updated timestamp. All nullable and additive; a pre-feature conversation reads all three as NULL.
        builder.Property(entity => entity.CompactionSummary)
               .HasColumnName("compaction_summary");

        builder.Property(entity => entity.CompactionSummaryCoversToSequence)
               .HasColumnName("compaction_summary_covers_to_sequence");

        builder.Property(entity => entity.CompactionSummaryUpdatedAtUtc)
               .HasColumnName("compaction_summary_updated_at_utc");

        // The conversation-list path, both variants: `purged = 0 [AND archived = 0]` ordered by `is_pinned DESC,
        // last_seen_utc DESC LIMIT n`. `archived` sorts LAST on purpose. Putting it second serves the active-only
        // query perfectly but leaves the show-all query (which does not constrain it) with a TEMP B-TREE over every
        // non-purged conversation — and because the list join runs a correlated last-message subquery per row, that
        // sort costs one subquery per conversation instead of `limit` of them. Trailing, it is still an index-resident
        // filter for the active query while both queries take the ordered reverse scan.
        builder.HasIndex(entity => new
               {
                   entity.Purged,
                   entity.IsPinned,
                   entity.LastSeenUtc,
                   entity.Archived
               })
               .HasDatabaseName("ix_conversations_list");

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
