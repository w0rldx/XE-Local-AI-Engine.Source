namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class DevWorkflowNodeRunConfiguration : IEntityTypeConfiguration<DevWorkflowNodeRun>
{
    public void Configure(EntityTypeBuilder<DevWorkflowNodeRun> builder)
    {
        builder.ToTable("dev_workflow_node_runs");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.RunId).HasColumnName("run_id");
        builder.Property(entity => entity.NodeKey).HasColumnName("node_key").HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.NodeType).HasColumnName("node_type").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.Attempt).HasColumnName("attempt");
        builder.Property(entity => entity.MaxAttempts).HasColumnName("max_attempts");
        builder.Property(entity => entity.SessionResumes).HasColumnName("session_resumes");
        builder.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.QueueReason).HasColumnName("queue_reason").HasMaxLength(64);
        builder.Property(entity => entity.PendingDecisionKind).HasColumnName("pending_decision_kind").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.Sequence).HasColumnName("sequence");

        // work_session_id, agent_definition_id, development_project_id and development_task_id all carry no foreign
        // key on purpose: they point into other families whose purge paths are raw SQL, and a node-run whose session
        // or task is gone must read back as recoverable state rather than fail the delete.
        builder.Property(entity => entity.WorkSessionId).HasColumnName("work_session_id");
        builder.Property(entity => entity.AgentDefinitionId).HasColumnName("agent_definition_id");
        builder.Property(entity => entity.DevelopmentProjectId).HasColumnName("development_project_id");
        builder.Property(entity => entity.DevelopmentTaskId).HasColumnName("development_task_id");

        builder.Property(entity => entity.InputJson).HasColumnName("input_json");
        builder.Property(entity => entity.OutputJson).HasColumnName("output_json");
        builder.Property(entity => entity.PolicyResolutionJson).HasColumnName("policy_resolution_json");
        builder.Property(entity => entity.MaterializedFromNodeRunId).HasColumnName("materialized_from_node_run_id");
        builder.Property(entity => entity.MaterializationIndex).HasColumnName("materialization_index");
        builder.Property(entity => entity.FailureClass).HasColumnName("failure_class").HasMaxLength(64);
        builder.Property(entity => entity.TerminalReason).HasColumnName("terminal_reason").HasMaxLength(1024);
        builder.Property(entity => entity.QueuedAtUtc).HasColumnName("queued_at_utc");
        builder.Property(entity => entity.StartedAtUtc).HasColumnName("started_at_utc");
        builder.Property(entity => entity.EndedAtUtc).HasColumnName("ended_at_utc");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");

        builder.HasOne<DevWorkflowRun>().WithMany().HasForeignKey(entity => entity.RunId).OnDelete(DeleteBehavior.Cascade);

        // The node-run's identity, not a secondary constraint: one row per node key, with Attempt incrementing in place.
        builder.HasIndex(entity => new
        {
            entity.RunId,
            entity.NodeKey
        }).IsUnique().HasDatabaseName("ux_dev_workflow_node_runs_run_node");

        builder.HasIndex(entity => new
        {
            entity.RunId,
            entity.Sequence
        }).HasDatabaseName("ix_dev_workflow_node_runs_run_sequence");
        builder.HasIndex(entity => entity.Status).HasDatabaseName("ix_dev_workflow_node_runs_status");

        // One session, one owner — and it makes the reverse lookup ("who owns this session?") a single indexed probe.
        builder.HasIndex(entity => entity.WorkSessionId)
               .IsUnique()
               .HasFilter("\"work_session_id\" IS NOT NULL")
               .HasDatabaseName("ux_dev_workflow_node_runs_work_session");

        builder.HasIndex(entity => entity.MaterializedFromNodeRunId).HasDatabaseName("ix_dev_workflow_node_runs_materialized_from");
        builder.HasIndex(entity => entity.DevelopmentTaskId).HasDatabaseName("ix_dev_workflow_node_runs_development_task");
    }
}
