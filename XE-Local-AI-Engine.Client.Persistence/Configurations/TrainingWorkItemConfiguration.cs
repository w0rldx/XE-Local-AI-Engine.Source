namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class TrainingWorkItemConfiguration : IEntityTypeConfiguration<TrainingWorkItem>
{
    public void Configure(EntityTypeBuilder<TrainingWorkItem> builder)
    {
        builder.ToTable("training_work_items", table => table.HasCheckConstraint("CK_training_work_items_attempt", "attempt = 1"));
        builder.HasKey(entity => entity.QueueSequence);

        // INTEGER PRIMARY KEY AUTOINCREMENT — the FIFO order survives restarts and never reuses a sequence.
        builder.Property(entity => entity.QueueSequence).HasColumnName("queue_sequence").ValueGeneratedOnAdd();
        builder.Property(entity => entity.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16);
        builder.Property(entity => entity.TargetId).HasColumnName("target_id");
        builder.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        builder.Property(entity => entity.Attempt).HasColumnName("attempt");
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(entity => entity.EnqueuedAtUtc).HasColumnName("enqueued_at_utc");
        builder.Property(entity => entity.StartedAtUtc).HasColumnName("started_at_utc");
        builder.Property(entity => entity.FinishedAtUtc).HasColumnName("finished_at_utc");
        builder.Property(entity => entity.ErrorMessage).HasColumnName("error_message").HasMaxLength(1024);

        // No foreign key: the target is a run id or an evaluation id depending on the kind, and SQLite cannot express a
        // polymorphic reference. The store checks the target exists before enqueueing.
        //
        // Same guarantee shape as ux_benchmark_work_items_run_kind — one work item per target per kind, ever. Since the
        // row is deleted with its target, "at most one" also means "at most one non-terminal".
        builder.HasIndex(entity => new
        {
            entity.TargetId,
            entity.Kind
        }).IsUnique().HasDatabaseName("ux_training_work_items_target_kind");

        // The claim scan reads the oldest queued row; this index is what makes it an index seek rather than a table scan.
        builder.HasIndex(entity => new
        {
            entity.Status,
            entity.QueueSequence
        }).HasDatabaseName("ix_training_work_items_status_sequence");
    }
}
