namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class IntegrationExecutionEventConfiguration : IEntityTypeConfiguration<IntegrationExecutionEvent>
{
    public void Configure(EntityTypeBuilder<IntegrationExecutionEvent> builder)
    {
        builder.ToTable("integration_execution_events");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.ExecutionId).HasColumnName("execution_id");
        builder.Property(entity => entity.Sequence).HasColumnName("sequence");
        builder.Property(entity => entity.EventType).HasColumnName("event_type").HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.DetailJson).HasColumnName("detail_json");
        builder.Property(entity => entity.OccurredAtUtc).HasColumnName("occurred_at_utc");

        // Enforced: the node connection runs with foreign keys on, so this cascade does remove an execution's events. ConversationFootprintPurge still deletes them
        // through an explicit subselect, because the conversation it starts from has no foreign key to the integration session that owns them.
        builder.HasOne<IntegrationExecution>().WithMany().HasForeignKey(entity => entity.ExecutionId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(entity => new
        {
            entity.ExecutionId,
            entity.Sequence
        }).IsUnique().HasDatabaseName("ux_integration_execution_events_execution_sequence");
    }
}
