namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class DevWorkflowRunConfiguration : IEntityTypeConfiguration<DevWorkflowRun>
{
    public void Configure(EntityTypeBuilder<DevWorkflowRun> builder)
    {
        builder.ToTable("dev_workflow_runs");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.WorkItemId).HasColumnName("work_item_id");
        builder.Property(entity => entity.DefinitionId).HasColumnName("definition_id");
        builder.Property(entity => entity.DefinitionVersion).HasColumnName("definition_version");
        builder.Property(entity => entity.DefinitionGraphHash).HasColumnName("definition_graph_hash").HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.GraphJson).HasColumnName("graph_json").IsRequired();
        builder.Property(entity => entity.GraphRevision).HasColumnName("graph_revision");
        builder.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.LastSequence).HasColumnName("last_sequence");

        // Plaintext: a closed token set the UI filters on — the failure TYPE, never its content.
        builder.Property(entity => entity.FailureClass).HasColumnName("failure_class").HasMaxLength(64);
        builder.Property(entity => entity.TerminalReason).HasColumnName("terminal_reason").HasMaxLength(512);
        builder.Property(entity => entity.StartedAtUtc).HasColumnName("started_at_utc");
        builder.Property(entity => entity.EndedAtUtc).HasColumnName("ended_at_utc");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();

        builder.HasOne<DevWorkflowWorkItem>().WithMany().HasForeignKey(entity => entity.WorkItemId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(entity => new
        {
            entity.WorkItemId,
            entity.CreatedAtUtc
        }).HasDatabaseName("ix_dev_workflow_runs_work_item");
        builder.HasIndex(entity => new
        {
            entity.Status,
            entity.UpdatedAtUtc
        }).HasDatabaseName("ix_dev_workflow_runs_status_updated");
        builder.HasIndex(entity => entity.DefinitionId).HasDatabaseName("ix_dev_workflow_runs_definition");

        // One live run per work item, enforced by a partial unique index rather than a check-then-insert in the
        // service: SQLite supports it and a database constraint cannot lose a race the way a read-modify-write can.
        builder.HasIndex(entity => entity.WorkItemId)
               .IsUnique()
               .HasFilter("\"status\" IN ('Pending','Running','Pausing','Paused','WaitingForApproval','Cancelling')")
               .HasDatabaseName("ux_dev_workflow_runs_live_per_work_item");
    }
}
