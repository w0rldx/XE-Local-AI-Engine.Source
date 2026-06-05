namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class ScheduledJobRunConfiguration : IEntityTypeConfiguration<ScheduledJobRun>
{
    public void Configure(EntityTypeBuilder<ScheduledJobRun> builder)
    {
        builder.ToTable("scheduled_job_runs");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.ScheduledJobId)
               .HasColumnName("scheduled_job_id");

        builder.Property(entity => entity.TemplateId)
               .HasColumnName("template_id");

        builder.Property(entity => entity.QuartzFireInstanceId)
               .HasColumnName("quartz_fire_instance_id");

        builder.Property(entity => entity.TriggeredBy)
               .HasColumnName("triggered_by");

        builder.Property(entity => entity.Status)
               .HasColumnName("status");

        builder.Property(entity => entity.ScheduledFireTimeUtc)
               .HasColumnName("scheduled_fire_time_utc");

        builder.Property(entity => entity.ActualFireTimeUtc)
               .HasColumnName("actual_fire_time_utc");

        builder.Property(entity => entity.CompletedAtUtc)
               .HasColumnName("completed_at_utc");

        builder.Property(entity => entity.DurationMs)
               .HasColumnName("duration_ms");

        builder.Property(entity => entity.Summary)
               .HasColumnName("summary");

        builder.Property(entity => entity.DetailsJson)
               .HasColumnName("details_json");

        builder.Property(entity => entity.ErrorMessage)
               .HasColumnName("error_message");

        builder.Property(entity => entity.ErrorDetails)
               .HasColumnName("error_details");

        builder.Property(entity => entity.CancellationRequestedAtUtc)
               .HasColumnName("cancellation_requested_at_utc");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.HasIndex(entity => new
        {
            entity.ScheduledJobId,
            entity.ActualFireTimeUtc
        });

        // The fire-instance id is the idempotency key for the upsert, so it is unique — but only among rows that
        // actually carry one (manual/system runs leave it null), hence the filtered unique index.
        builder.HasIndex(entity => entity.QuartzFireInstanceId)
               .IsUnique()
               .HasFilter("quartz_fire_instance_id IS NOT NULL");

        // A run intentionally has NO enforced FK to its definition: runs outlive definitions (a removed/soft-deleted
        // definition must not cascade away its run history). Same intentional no-FK precedent as conversation->definition.
    }
}
