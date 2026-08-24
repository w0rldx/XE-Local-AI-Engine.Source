namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class AgentWorkSessionCheckpointConfiguration : IEntityTypeConfiguration<AgentWorkSessionCheckpoint>
{
    public void Configure(EntityTypeBuilder<AgentWorkSessionCheckpoint> builder)
    {
        builder.ToTable("agent_work_session_checkpoints");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.SessionId).HasColumnName("session_id");
        builder.Property(entity => entity.Sequence).HasColumnName("sequence");
        builder.Property(entity => entity.Step).HasColumnName("step");
        builder.Property(entity => entity.Summary).HasColumnName("summary");
        builder.Property(entity => entity.StateJson).HasColumnName("state_json").IsRequired();
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.HasOne<AgentWorkSession>().WithMany().HasForeignKey(entity => entity.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(entity => new
        {
            entity.SessionId,
            entity.Sequence
        }).HasDatabaseName("ix_agent_work_session_checkpoints_session_sequence");
    }
}
