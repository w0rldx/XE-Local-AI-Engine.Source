namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class IntegrationSessionConfiguration : IEntityTypeConfiguration<IntegrationSession>
{
    public void Configure(EntityTypeBuilder<IntegrationSession> builder)
    {
        builder.ToTable("integration_sessions");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.TriggerId).HasColumnName("trigger_id");
        builder.Property(entity => entity.PrincipalId).HasColumnName("principal_id");
        builder.Property(entity => entity.ConversationId).HasColumnName("conversation_id");
        builder.Property(entity => entity.AgentDefinitionId).HasColumnName("agent_definition_id");
        builder.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.LastActivityUtc).HasColumnName("last_activity_utc");
        builder.Property(entity => entity.ExecutionCount).HasColumnName("execution_count");
        builder.Property(entity => entity.LastSequence).HasColumnName("last_sequence");

        // conversation_id and agent_definition_id carry no foreign key, for the reason AgentWorkSessionConfiguration
        // gives — the conversation purge path is raw ADO SQL and must not fail on a dangling reference — and for a
        // sharper one here: the session row is written BEFORE its conversation exists, because the accept transaction
        // commits first and the caller creates the conversation afterwards (ADR 0008 Decision §3).
        builder.HasIndex(entity => entity.ConversationId).IsUnique().HasDatabaseName("ux_integration_sessions_conversation_id");
        builder.HasIndex(entity => entity.TriggerId).HasDatabaseName("ix_integration_sessions_trigger");
        builder.HasIndex(entity => new
        {
            entity.Status,
            entity.LastActivityUtc
        }).HasDatabaseName("ix_integration_sessions_status_activity");

        // No index on principal_id: the ownership check is `WHERE id = ? AND principal_id = ?`, already a primary-key
        // seek with one equality filter.
    }
}
