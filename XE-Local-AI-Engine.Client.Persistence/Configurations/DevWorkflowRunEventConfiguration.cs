namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class DevWorkflowRunEventConfiguration : IEntityTypeConfiguration<DevWorkflowRunEvent>
{
    public void Configure(EntityTypeBuilder<DevWorkflowRunEvent> builder)
    {
        builder.ToTable("dev_workflow_run_events");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.RunId).HasColumnName("run_id");
        builder.Property(entity => entity.NodeRunId).HasColumnName("node_run_id");
        builder.Property(entity => entity.Sequence).HasColumnName("sequence");
        builder.Property(entity => entity.EventType).HasColumnName("event_type").HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.DetailJson).HasColumnName("detail_json");
        builder.Property(entity => entity.OperationId).HasColumnName("operation_id");
        builder.Property(entity => entity.Outcome).HasColumnName("outcome").HasMaxLength(64);
        builder.Property(entity => entity.OccurredAtUtc).HasColumnName("occurred_at_utc");

        builder.HasOne<DevWorkflowRun>().WithMany().HasForeignKey(entity => entity.RunId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(entity => new
        {
            entity.RunId,
            entity.Sequence
        }).IsUnique().HasDatabaseName("ux_dev_workflow_run_events_run_sequence");

        // One event per operation id — the idempotency key the store resolves query-first. No operation_phase column:
        // a phase with exactly one value per operation is a column nobody reads.
        builder.HasIndex(entity => new
               {
                   entity.RunId,
                   entity.OperationId
               })
               .IsUnique()
               .HasFilter("\"operation_id\" IS NOT NULL")
               .HasDatabaseName("ux_dev_workflow_run_events_operation");

        builder.HasIndex(entity => entity.NodeRunId).HasDatabaseName("ix_dev_workflow_run_events_node_run");
    }
}
