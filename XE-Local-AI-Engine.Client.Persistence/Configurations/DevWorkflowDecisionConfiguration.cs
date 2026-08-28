namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class DevWorkflowDecisionConfiguration : IEntityTypeConfiguration<DevWorkflowDecision>
{
    public void Configure(EntityTypeBuilder<DevWorkflowDecision> builder)
    {
        builder.ToTable("dev_workflow_decisions");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.RunId).HasColumnName("run_id");
        builder.Property(entity => entity.NodeRunId).HasColumnName("node_run_id");
        builder.Property(entity => entity.Attempt).HasColumnName("attempt");
        builder.Property(entity => entity.Decision).HasColumnName("decision").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.Comment).HasColumnName("comment");
        builder.Property(entity => entity.PayloadJson).HasColumnName("payload_json");
        builder.Property(entity => entity.DecidedBySubject).HasColumnName("decided_by_subject").HasMaxLength(128);
        builder.Property(entity => entity.OperationId).HasColumnName("operation_id");
        builder.Property(entity => entity.Sequence).HasColumnName("sequence");
        builder.Property(entity => entity.DecidedAtUtc).HasColumnName("decided_at_utc");

        builder.HasOne<DevWorkflowRun>().WithMany().HasForeignKey(entity => entity.RunId).OnDelete(DeleteBehavior.Cascade);

        // One decision per node-run ATTEMPT, not per node-run: a node-run legitimately accumulates several over its
        // life (fail, Retry at attempt 1, Approve at attempt 2), and uniqueness on node_run_id alone would reject the
        // second one.
        builder.HasIndex(entity => new
        {
            entity.NodeRunId,
            entity.Attempt
        }).IsUnique().HasDatabaseName("ux_dev_workflow_decisions_node_run_attempt");

        builder.HasIndex(entity => new
        {
            entity.RunId,
            entity.OperationId
        }).IsUnique().HasDatabaseName("ux_dev_workflow_decisions_operation");

        builder.HasIndex(entity => new
        {
            entity.RunId,
            entity.Sequence
        }).HasDatabaseName("ix_dev_workflow_decisions_run_sequence");
    }
}
