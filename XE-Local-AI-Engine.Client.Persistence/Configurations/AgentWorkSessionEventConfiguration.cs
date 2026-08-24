namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class AgentWorkSessionEventConfiguration : IEntityTypeConfiguration<AgentWorkSessionEvent>
{
    public void Configure(EntityTypeBuilder<AgentWorkSessionEvent> builder)
    {
        builder.ToTable("agent_work_session_events");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.SessionId).HasColumnName("session_id");
        builder.Property(entity => entity.Sequence).HasColumnName("sequence");
        builder.Property(entity => entity.Step).HasColumnName("step");
        builder.Property(entity => entity.EventType).HasColumnName("event_type").HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.DetailJson).HasColumnName("detail_json");
        builder.Property(entity => entity.OperationId).HasColumnName("operation_id");
        builder.Property(entity => entity.Outcome).HasColumnName("outcome").HasMaxLength(64);
        builder.Property(entity => entity.OccurredAtUtc).HasColumnName("occurred_at_utc");
        builder.HasOne<AgentWorkSession>().WithMany().HasForeignKey(entity => entity.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(entity => new
        {
            entity.SessionId,
            entity.Sequence
        }).IsUnique().HasDatabaseName("ux_agent_work_session_events_session_sequence");

        // One event per operation id. Unlike development_events there is no third phase column: a phase with exactly
        // one value per operation is a column nobody reads, so the caller derives a distinct operation id per event
        // instead and the store resolves it query-first.
        builder.HasIndex(entity => new
               {
                   entity.SessionId,
                   entity.OperationId
               })
               .IsUnique()
               .HasFilter("operation_id IS NOT NULL")
               .HasDatabaseName("ux_agent_work_session_events_operation");
    }
}
