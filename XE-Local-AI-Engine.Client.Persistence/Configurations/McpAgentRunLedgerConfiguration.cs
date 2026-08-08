namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class McpAgentRunLedgerConfiguration : IEntityTypeConfiguration<McpAgentRunLedger>
{
    public void Configure(EntityTypeBuilder<McpAgentRunLedger> builder)
    {
        builder.ToTable("mcp_agent_run_ledger", table =>
        {
            table.HasCheckConstraint("CK_mcp_agent_run_ledger_singleton", "id = 1");
            table.HasCheckConstraint("CK_mcp_agent_run_ledger_accounting_version", "accounting_version = 1");
            table.HasCheckConstraint("CK_mcp_agent_run_ledger_nonnegative",
                "nonterminal_run_count >= 0 AND queued_run_count >= 0 AND running_run_count >= 0 AND nonterminal_run_count = queued_run_count + running_run_count AND identity_count >= 0 AND active_payload_bytes >= 0 AND tombstone_logical_bytes >= 0");
        });
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(entity => entity.AccountingVersion).HasColumnName("accounting_version");
        builder.Property(entity => entity.NonterminalRunCount).HasColumnName("nonterminal_run_count");
        builder.Property(entity => entity.QueuedRunCount).HasColumnName("queued_run_count");
        builder.Property(entity => entity.RunningRunCount).HasColumnName("running_run_count");
        builder.Property(entity => entity.IdentityCount).HasColumnName("identity_count");
        builder.Property(entity => entity.ActivePayloadBytes).HasColumnName("active_payload_bytes");
        builder.Property(entity => entity.TombstoneLogicalBytes).HasColumnName("tombstone_logical_bytes");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");

        builder.HasData(new McpAgentRunLedger
        {
            Id = 1,
            AccountingVersion = 1,
            NonterminalRunCount = 0,
            QueuedRunCount = 0,
            RunningRunCount = 0,
            IdentityCount = 0,
            ActivePayloadBytes = 0,
            TombstoneLogicalBytes = 0,
            UpdatedAtUtc = 0
        });
    }
}
