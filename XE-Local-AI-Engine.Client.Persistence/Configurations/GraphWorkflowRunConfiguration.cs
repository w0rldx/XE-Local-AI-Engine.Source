namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class GraphWorkflowRunConfiguration : IEntityTypeConfiguration<GraphWorkflowRun>
{
    public void Configure(EntityTypeBuilder<GraphWorkflowRun> builder)
    {
        builder.ToTable("graph_workflow_runs");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.RequestId).HasColumnName("request_id");

        // No foreign key to the definition on purpose: a definition may be hard-deleted once no live run pins it, and
        // the runs that already finished keep their own pinned graph rather than being deleted with it.
        builder.Property(entity => entity.DefinitionId).HasColumnName("definition_id");
        builder.Property(entity => entity.DefinitionVersion).HasColumnName("definition_version");
        builder.Property(entity => entity.GraphHash).HasColumnName("graph_hash").HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);

        // Persisted as text, like the Dev Workflow column it mirrors: a closed token set the UI filters on. Storing
        // the enum by ordinal would make the durable log unreadable the first time a member is inserted.
        builder.Property(entity => entity.FailureClass).HasColumnName("failure_class").HasConversion<string>().HasMaxLength(64);
        builder.Property(entity => entity.GraphJson).HasColumnName("graph_json").IsRequired();
        builder.Property(entity => entity.InputJson).HasColumnName("input_json");
        builder.Property(entity => entity.OutputJson).HasColumnName("output_json");
        builder.Property(entity => entity.Seq).HasColumnName("seq");
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(entity => entity.CancelRequestedAtUtc).HasColumnName("cancel_requested_at_utc");
        builder.Property(entity => entity.StartedAtUtc).HasColumnName("started_at_utc");
        builder.Property(entity => entity.CompletedAtUtc).HasColumnName("completed_at_utc");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");

        // The idempotency key is a database constraint rather than a check-then-insert in the service: SQLite supports
        // it and a unique index cannot lose a race the way a read-modify-write can.
        builder.HasIndex(entity => entity.RequestId).IsUnique().HasDatabaseName("ux_graph_workflow_runs_request_id");

        builder.HasIndex(entity => entity.DefinitionId).HasDatabaseName("ix_graph_workflow_runs_definition");
        builder.HasIndex(entity => new
        {
            entity.Status,
            entity.CreatedAtUtc
        }).HasDatabaseName("ix_graph_workflow_runs_status_created");
    }
}
