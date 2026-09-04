namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class GraphWorkflowNodeRunConfiguration : IEntityTypeConfiguration<GraphWorkflowNodeRun>
{
    public void Configure(EntityTypeBuilder<GraphWorkflowNodeRun> builder)
    {
        builder.ToTable("graph_workflow_node_runs");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.RunId).HasColumnName("run_id");
        builder.Property(entity => entity.NodeKey).HasColumnName("node_key").HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.Attempt).HasColumnName("attempt");
        builder.Property(entity => entity.PendingDecisionKind).HasColumnName("pending_decision_kind").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.DecisionOperationId).HasColumnName("decision_operation_id");
        builder.Property(entity => entity.DecidedBySubject).HasColumnName("decided_by_subject");

        // Text, as on the run row and for the same reason.
        builder.Property(entity => entity.FailureClass).HasColumnName("failure_class").HasConversion<string>().HasMaxLength(64);
        builder.Property(entity => entity.Error).HasColumnName("error");
        builder.Property(entity => entity.InputJson).HasColumnName("input_json");
        builder.Property(entity => entity.OutputJson).HasColumnName("output_json");

        // No foreign key: the invocation lives in another family whose purge path is its own, and a node run whose
        // invocation is gone must read back as recoverable state rather than fail the delete.
        builder.Property(entity => entity.InvocationId).HasColumnName("invocation_id");
        builder.Property(entity => entity.StartedAtUtc).HasColumnName("started_at_utc");
        builder.Property(entity => entity.CompletedAtUtc).HasColumnName("completed_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");

        builder.HasOne<GraphWorkflowRun>().WithMany().HasForeignKey(entity => entity.RunId).OnDelete(DeleteBehavior.Cascade);

        // The node run's identity, not a secondary constraint: one row per node key, with Attempt incrementing in place.
        builder.HasIndex(entity => new
        {
            entity.RunId,
            entity.NodeKey
        }).IsUnique().HasDatabaseName("ux_graph_workflow_node_runs_run_node");

        // One decision per operation id, run-wide — the decide endpoint's idempotency key. Filtered, because every row
        // that has not been decided carries a null here and null is not unique.
        builder.HasIndex(entity => new
               {
                   entity.RunId,
                   entity.DecisionOperationId
               })
               .IsUnique()
               .HasFilter("\"decision_operation_id\" IS NOT NULL")
               .HasDatabaseName("ux_graph_workflow_node_runs_decision_operation");

        builder.HasIndex(entity => entity.Status).HasDatabaseName("ix_graph_workflow_node_runs_status");
    }
}
