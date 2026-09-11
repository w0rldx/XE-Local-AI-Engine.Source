namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class ExternalAppInstanceEventConfiguration : IEntityTypeConfiguration<ExternalAppInstanceEvent>
{
    public void Configure(EntityTypeBuilder<ExternalAppInstanceEvent> builder)
    {
        builder.ToTable("external_app_instance_events");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.InstanceId).HasColumnName("instance_id");
        builder.Property(entity => entity.Sequence).HasColumnName("sequence");
        builder.Property(entity => entity.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.DetailJson).HasColumnName("detail_json");
        builder.Property(entity => entity.OccurredAtUtc).HasColumnName("occurred_at_utc");

        // Declared for parity with integration_execution_events and for tooling; decorative at runtime, because the
        // node-sqlite connection leaves PRAGMA foreign_keys off, so ON DELETE CASCADE never fires and the store's own
        // ordered delete is the real teardown.
        builder.HasOne<ExternalAppInstance>().WithMany().HasForeignKey(entity => entity.InstanceId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(entity => new
        {
            entity.InstanceId,
            entity.Sequence
        }).IsUnique().HasDatabaseName("ux_external_app_instance_events_instance_sequence");
    }
}
