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

        // Enforced: the node connection runs with foreign keys on, so this cascade does remove an instance's events. The store still deletes them explicitly, inside
        // the same transaction as the instance row, so the teardown is stated in one place rather than split between the store and the schema.
        builder.HasOne<ExternalAppInstance>().WithMany().HasForeignKey(entity => entity.InstanceId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(entity => new
        {
            entity.InstanceId,
            entity.Sequence
        }).IsUnique().HasDatabaseName("ux_external_app_instance_events_instance_sequence");
    }
}
