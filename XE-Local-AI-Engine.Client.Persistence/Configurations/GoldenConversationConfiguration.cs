namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class GoldenConversationConfiguration : IEntityTypeConfiguration<GoldenConversation>
{
    public void Configure(EntityTypeBuilder<GoldenConversation> builder)
    {
        builder.ToTable("golden_conversations");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.AgentDefinitionId)
               .HasColumnName("agent_definition_id");

        builder.Property(entity => entity.Title)
               .HasColumnName("title");

        builder.Property(entity => entity.InputTurns)
               .HasColumnName("input_turns");

        builder.Property(entity => entity.Assertion)
               .HasColumnName("assertion");

        builder.Property(entity => entity.Rubric)
               .HasColumnName("rubric");

        builder.Property(entity => entity.Enabled)
               .HasColumnName("enabled");

        // Harvest provenance — additive columns. Plaintext (an enum + two ids), not encrypted; the sensitive harvested
        // text reuses the already-encrypted input_turns/rubric columns.
        builder.Property(entity => entity.Source)
               .HasColumnName("source")
               .HasDefaultValue(GoldenConversationSource.Manual);

        builder.Property(entity => entity.SourceMessageId)
               .HasColumnName("source_message_id");

        builder.Property(entity => entity.SourceConversationId)
               .HasColumnName("source_conversation_id");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.UpdatedAtUtc)
               .HasColumnName("updated_at_utc");

        builder.HasIndex(entity => entity.AgentDefinitionId);

        // A golden conversation belongs to its agent's evaluation set, so the FK cascades: deleting an agent removes its
        // golden cases — same layout as playbook_actions.
        builder.HasOne<AgentDefinition>()
               .WithMany()
               .HasForeignKey(entity => entity.AgentDefinitionId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
