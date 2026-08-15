namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class DatasetGenerationWorkItemConfiguration : IEntityTypeConfiguration<DatasetGenerationWorkItem>
{
    public void Configure(EntityTypeBuilder<DatasetGenerationWorkItem> builder)
    {
        builder.ToTable("dataset_generation_work_items",
            table => table.HasCheckConstraint("CK_dataset_generation_work_items_attempt", "attempt = 1"));
        builder.HasKey(entity => entity.QueueSequence);

        // INTEGER PRIMARY KEY AUTOINCREMENT — the FIFO order survives restarts and never reuses a sequence.
        builder.Property(entity => entity.QueueSequence).HasColumnName("queue_sequence").ValueGeneratedOnAdd();
        builder.Property(entity => entity.DatasetId).HasColumnName("dataset_id");
        builder.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        builder.Property(entity => entity.Attempt).HasColumnName("attempt");
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(entity => entity.EnqueuedAtUtc).HasColumnName("enqueued_at_utc");
        builder.Property(entity => entity.StartedAtUtc).HasColumnName("started_at_utc");
        builder.Property(entity => entity.FinishedAtUtc).HasColumnName("finished_at_utc");
        builder.Property(entity => entity.ErrorMessage).HasColumnName("error_message").HasMaxLength(1024);
        builder.HasOne<TrainingDataset>()
               .WithMany()
               .HasForeignKey(entity => entity.DatasetId)
               .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(entity => entity.DatasetId).IsUnique().HasDatabaseName("ux_dataset_generation_work_items_dataset");

        // The claim scan reads the oldest queued row; this index is what makes it an index seek rather than a table scan.
        builder.HasIndex(entity => new
        {
            entity.Status,
            entity.QueueSequence
        }).HasDatabaseName("ix_dataset_generation_work_items_status_sequence");
    }
}
