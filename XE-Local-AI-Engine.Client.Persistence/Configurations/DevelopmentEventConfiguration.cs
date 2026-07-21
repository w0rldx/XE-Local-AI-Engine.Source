namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class DevelopmentEventConfiguration : IEntityTypeConfiguration<DevelopmentEvent>
{
    public void Configure(EntityTypeBuilder<DevelopmentEvent> builder)
    {
        builder.ToTable("development_events");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.ProjectId).HasColumnName("project_id");
        builder.Property(entity => entity.TaskId).HasColumnName("task_id");
        builder.Property(entity => entity.AttemptId).HasColumnName("attempt_id");
        builder.Property(entity => entity.Sequence).HasColumnName("sequence");
        builder.Property(entity => entity.EventType).HasColumnName("event_type").HasMaxLength(128);
        builder.Property(entity => entity.OccurredAtUtc).HasColumnName("occurred_at_utc");
        builder.Property(entity => entity.DetailJson).HasColumnName("detail_json");
        builder.Property(entity => entity.OperationId).HasColumnName("operation_id");
        builder.Property(entity => entity.OperationPhase).HasColumnName("operation_phase").HasMaxLength(64);
        builder.Property(entity => entity.Outcome).HasColumnName("outcome").HasMaxLength(64);
        builder.Property(entity => entity.ResultMetadataJson).HasColumnName("result_metadata_json");
        builder.HasIndex(entity => new { entity.ProjectId, entity.Sequence }).IsUnique();
        builder.HasIndex(entity => new { entity.ProjectId, entity.OperationId, entity.OperationPhase })
               .IsUnique()
               .HasFilter("operation_id IS NOT NULL AND operation_phase IS NOT NULL");
        builder.HasOne<DevelopmentProject>().WithMany().HasForeignKey(entity => entity.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<DevelopmentTask>().WithMany().HasForeignKey(entity => entity.TaskId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<DevelopmentAttempt>().WithMany().HasForeignKey(entity => entity.AttemptId).OnDelete(DeleteBehavior.SetNull);
    }
}
