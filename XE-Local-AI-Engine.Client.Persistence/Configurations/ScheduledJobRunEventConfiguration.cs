namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class ScheduledJobRunEventConfiguration : IEntityTypeConfiguration<ScheduledJobRunEvent>
{
    public void Configure(EntityTypeBuilder<ScheduledJobRunEvent> builder)
    {
        builder.ToTable("scheduled_job_run_events");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.RunId)
               .HasColumnName("run_id");

        builder.Property(entity => entity.Sequence)
               .HasColumnName("sequence");

        builder.Property(entity => entity.Level)
               .HasColumnName("level");

        builder.Property(entity => entity.Message)
               .HasColumnName("message");

        builder.Property(entity => entity.DataJson)
               .HasColumnName("data_json");

        builder.Property(entity => entity.OccurredAtUtc)
               .HasColumnName("occurred_at_utc");

        builder.HasIndex(entity => new
               {
                   entity.RunId,
                   entity.Sequence
               })
               .IsUnique();

        // An event is meaningless without its owning run, so the FK cascades: deleting a run removes its events.
        builder.HasOne<ScheduledJobRun>()
               .WithMany()
               .HasForeignKey(entity => entity.RunId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
