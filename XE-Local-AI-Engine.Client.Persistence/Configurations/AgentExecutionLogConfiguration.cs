namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class AgentExecutionLogConfiguration : IEntityTypeConfiguration<AgentExecutionLog>
{
    public void Configure(EntityTypeBuilder<AgentExecutionLog> builder)
    {
        builder.ToTable("agent_execution_logs");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.AgentDefinitionId)
               .HasColumnName("agent_definition_id");

        builder.Property(entity => entity.ConversationId)
               .HasColumnName("conversation_id");

        builder.Property(entity => entity.MessageId)
               .HasColumnName("message_id");

        builder.Property(entity => entity.ModelName)
               .HasColumnName("model_name");

        builder.Property(entity => entity.ConfigHash)
               .HasColumnName("config_hash");

        builder.Property(entity => entity.LatencyMs)
               .HasColumnName("latency_ms");

        builder.Property(entity => entity.PromptTokens)
               .HasColumnName("prompt_tokens");

        builder.Property(entity => entity.CompletionTokens)
               .HasColumnName("completion_tokens");

        builder.Property(entity => entity.Success)
               .HasColumnName("success");

        builder.Property(entity => entity.ErrorClass)
               .HasColumnName("error_class");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        // The paged list-by-agent read filters by agent and orders newest-first, so index the pair. No FK to
        // agent_definitions: a run log is diagnostic telemetry that should outlive the definition (mirrors the no-FK
        // conversation->definition choice), so deleting an agent must not cascade-delete its execution history.
        builder.HasIndex(entity => new
        {
            entity.AgentDefinitionId,
            entity.CreatedAtUtc
        });
    }
}
