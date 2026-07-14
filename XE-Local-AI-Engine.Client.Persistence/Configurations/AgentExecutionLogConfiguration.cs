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

        // Discriminator + envelope shape version. Both default to 0 at the DB level so the rows that predate the run
        // envelope (all adaptive-memory diagnostics) backfill to kind 0 / version 0 on migration.
        builder.Property(entity => entity.RecordKind)
               .HasColumnName("record_kind")
               .HasDefaultValue(0);

        builder.Property(entity => entity.SchemaVersion)
               .HasColumnName("schema_version")
               .HasDefaultValue(0);

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

        builder.Property(entity => entity.InvocationId)
               .HasColumnName("invocation_id");

        builder.Property(entity => entity.RequestId)
               .HasColumnName("request_id");

        builder.Property(entity => entity.TerminalStatus)
               .HasColumnName("terminal_status");

        builder.Property(entity => entity.TraceId)
               .HasColumnName("trace_id");

        builder.Property(entity => entity.ContentChunkCount)
               .HasColumnName("content_chunk_count");

        builder.Property(entity => entity.ReasoningChunkCount)
               .HasColumnName("reasoning_chunk_count");

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
