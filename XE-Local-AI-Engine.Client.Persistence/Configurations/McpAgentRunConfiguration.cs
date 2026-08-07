namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class McpAgentRunConfiguration : IEntityTypeConfiguration<McpAgentRun>
{
    public void Configure(EntityTypeBuilder<McpAgentRun> builder)
    {
        builder.ToTable("mcp_agent_runs", table =>
        {
            table.HasCheckConstraint("CK_mcp_agent_runs_accounting_version", "accounting_version = 1");
            table.HasCheckConstraint("CK_mcp_agent_runs_nonnegative",
                "version >= 0 AND reserved_active_payload_bytes >= 0 AND active_payload_bytes >= 0 AND tombstone_logical_bytes >= 0");
        });
        builder.HasKey(entity => entity.RequestId);

        builder.Property(entity => entity.RequestId).HasColumnName("request_id").ValueGeneratedNever();
        builder.Property(entity => entity.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(32).IsRequired();
        builder.Property(entity => entity.AccountingVersion).HasColumnName("accounting_version");
        builder.Property(entity => entity.Status).HasColumnName("status");
        builder.Property(entity => entity.Version).HasColumnName("version");
        builder.Property(entity => entity.ClaimToken).HasColumnName("claim_token");
        builder.Property(entity => entity.StopReason).HasColumnName("stop_reason");
        builder.Property(entity => entity.StopRequestedAtUtc).HasColumnName("stop_requested_at_utc");
        builder.Property(entity => entity.AgentDefinitionId).HasColumnName("agent_definition_id");
        builder.Property(entity => entity.AgentDefinitionVersion).HasColumnName("agent_definition_version");
        builder.Property(entity => entity.ModelId).HasColumnName("model_id").HasMaxLength(1024);
        builder.Property(entity => entity.ModelOverrideId).HasColumnName("model_override_id").HasMaxLength(1024);
        builder.Property(entity => entity.WorkspaceId).HasColumnName("workspace_id");
        builder.Property(entity => entity.BindingFingerprint).HasColumnName("binding_fingerprint").HasMaxLength(32);
        builder.Property(entity => entity.TaskPayload).HasColumnName("task_payload");
        builder.Property(entity => entity.InstructionsPayload).HasColumnName("instructions_payload");
        builder.Property(entity => entity.ResultPayload).HasColumnName("result_payload");
        builder.Property(entity => entity.DisplayPayload).HasColumnName("display_payload");
        builder.Property(entity => entity.FailureCode).HasColumnName("failure_code").HasMaxLength(128);
        builder.Property(entity => entity.ReservedActivePayloadBytes).HasColumnName("reserved_active_payload_bytes");
        builder.Property(entity => entity.ActivePayloadBytes).HasColumnName("active_payload_bytes");
        builder.Property(entity => entity.TombstoneLogicalBytes).HasColumnName("tombstone_logical_bytes");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.ClaimedAtUtc).HasColumnName("claimed_at_utc");
        builder.Property(entity => entity.CompletedAtUtc).HasColumnName("completed_at_utc");
        builder.Property(entity => entity.PayloadExpiresAtUtc).HasColumnName("payload_expires_at_utc");
        builder.Property(entity => entity.CompactedAtUtc).HasColumnName("compacted_at_utc");

        builder.HasIndex(entity => new { entity.Status, entity.CreatedAtUtc });
        builder.HasIndex(entity => entity.PayloadExpiresAtUtc);
    }
}
