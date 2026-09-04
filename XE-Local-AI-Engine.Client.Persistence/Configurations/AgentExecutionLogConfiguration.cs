namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

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

        // Fine-grained runtime provider (non-sensitive category label). Non-null with a DB-level default so existing rows
        // and any envelope written without a resolved provider backfill to 'unknown' — the envelope INSERT omits this
        // column from its explicit column list, so SQLite applies this default on write.
        builder.Property(entity => entity.Provider)
               .HasColumnName("provider")
               .HasDefaultValue(AgentUsageProviders.Unknown);

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

        builder.Property(entity => entity.ReasoningTokens)
               .HasColumnName("reasoning_tokens");

        builder.Property(entity => entity.TotalTokens)
               .HasColumnName("total_tokens");

        builder.Property(entity => entity.StartedAtUtc)
               .HasColumnName("started_at_utc");

        // Tool-schema token telemetry, both nullable with no backfill: a pre-migration envelope row simply reports
        // null. SQLite INTEGER is 64-bit either way, so the widened cumulative column costs the schema nothing.
        builder.Property(entity => entity.ToolSchemaTokens)
               .HasColumnName("tool_schema_tokens");

        builder.Property(entity => entity.MaxToolSchemaTokens)
               .HasColumnName("max_tool_schema_tokens");

        // Adaptive-effort dispatch telemetry, both nullable with no backfill: a pre-migration envelope row, and every
        // turn that was not authored `auto`, simply reports null. Closed-vocabulary labels, so no length constraint
        // buys anything the writer does not already guarantee.
        builder.Property(entity => entity.DispatchedTier)
               .HasColumnName("dispatched_tier");

        builder.Property(entity => entity.AuthoredEffort)
               .HasColumnName("authored_effort");

        // The paged list-by-agent read filters by agent and orders newest-first, so index the pair. No FK to
        // agent_definitions: a run log is diagnostic telemetry that should outlive the definition (mirrors the no-FK
        // conversation->definition choice), so deleting an agent must not cascade-delete its execution history.
        builder.HasIndex(entity => new
        {
            entity.AgentDefinitionId,
            entity.CreatedAtUtc
        });

        // Deterministic identity for a run envelope: exactly one envelope row per terminalized assistant message. The
        // filtered UNIQUE index is the DB-level guard behind the WHERE NOT EXISTS the atomic terminalize write and the
        // startup reconcile both use (a retry or a crash-recovery backfill can never duplicate), and gives a crash
        // between the message commit and the envelope write a recoverable key. The
        // filter scopes it to run-envelope rows so the memory-diagnostics rows, which may repeat a message id, are
        // unaffected. SQLite treats null message ids as distinct, so an envelope missing one (should not occur) never trips it.
        builder.HasIndex(entity => entity.MessageId)
               .IsUnique()
               .HasFilter($"record_kind = {(int)AgentExecutionLogRecordKind.ChatRunEnvelope}")
               .HasDatabaseName("ix_agent_execution_logs_envelope_message_id");
    }
}
