namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class ScheduledJobDefinitionConfiguration : IEntityTypeConfiguration<ScheduledJobDefinition>
{
    public void Configure(EntityTypeBuilder<ScheduledJobDefinition> builder)
    {
        builder.ToTable("scheduled_job_definitions");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.TemplateId)
               .HasColumnName("template_id");

        builder.Property(entity => entity.DisplayName)
               .HasColumnName("display_name");

        builder.Property(entity => entity.Description)
               .HasColumnName("description");

        builder.Property(entity => entity.Enabled)
               .HasColumnName("enabled")
               .HasDefaultValue(true);

        builder.Property(entity => entity.ScheduleKind)
               .HasColumnName("schedule_kind");

        builder.Property(entity => entity.CronExpression)
               .HasColumnName("cron_expression");

        builder.Property(entity => entity.IntervalSeconds)
               .HasColumnName("interval_seconds");

        builder.Property(entity => entity.RepeatCount)
               .HasColumnName("repeat_count");

        builder.Property(entity => entity.StartAtUtc)
               .HasColumnName("start_at_utc");

        builder.Property(entity => entity.EndAtUtc)
               .HasColumnName("end_at_utc");

        builder.Property(entity => entity.TimeZoneId)
               .HasColumnName("time_zone_id")
               .HasDefaultValue("UTC");

        builder.Property(entity => entity.MisfirePolicy)
               .HasColumnName("misfire_policy");

        builder.Property(entity => entity.PreventOverlap)
               .HasColumnName("prevent_overlap")
               .HasDefaultValue(false);

        builder.Property(entity => entity.MaxRuntimeSeconds)
               .HasColumnName("max_runtime_seconds");

        builder.Property(entity => entity.ParameterJson)
               .HasColumnName("parameter_json");

        builder.Property(entity => entity.CreatedBy)
               .HasColumnName("created_by");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.UpdatedAtUtc)
               .HasColumnName("updated_at_utc");

        builder.Property(entity => entity.DisabledAtUtc)
               .HasColumnName("disabled_at_utc");

        builder.Property(entity => entity.DeletedAtUtc)
               .HasColumnName("deleted_at_utc");

        builder.HasIndex(entity => new
        {
            entity.TemplateId,
            entity.Enabled
        });
    }
}
