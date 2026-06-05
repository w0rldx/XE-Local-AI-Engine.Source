namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class NodeToolEventConfiguration : IEntityTypeConfiguration<NodeToolEvent>
{
    public void Configure(EntityTypeBuilder<NodeToolEvent> builder)
    {
        builder.ToTable("tool_events");
        builder.HasKey(entity => entity.ToolCallId);

        builder.Property(entity => entity.ToolCallId)
               .HasColumnName("tool_call_id");

        builder.Property(entity => entity.ConversationId)
               .HasColumnName("conversation_id");

        builder.Property(entity => entity.ToolName)
               .HasColumnName("tool_name");

        builder.Property(entity => entity.PlaintextArgs)
               .HasColumnName("plaintext_args");

        builder.Property(entity => entity.PlaintextResult)
               .HasColumnName("plaintext_result");

        builder.Property(entity => entity.Status)
               .HasColumnName("status");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");
    }
}
