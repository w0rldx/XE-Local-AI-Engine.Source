namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class AgentWorkSessionTaskConfiguration : IEntityTypeConfiguration<AgentWorkSessionTask>
{
    public void Configure(EntityTypeBuilder<AgentWorkSessionTask> builder)
    {
        builder.ToTable("agent_work_session_tasks");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.SessionId).HasColumnName("session_id");
        builder.Property(entity => entity.ParentTaskId).HasColumnName("parent_task_id");
        builder.Property(entity => entity.Sequence).HasColumnName("sequence");
        builder.Property(entity => entity.Title).HasColumnName("title").IsRequired();
        builder.Property(entity => entity.Detail).HasColumnName("detail");
        builder.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.BlockedReason).HasColumnName("blocked_reason");
        builder.Property(entity => entity.Origin).HasColumnName("origin").HasConversion<string>().HasMaxLength(16);
        builder.Property(entity => entity.CreatedStep).HasColumnName("created_step");
        builder.Property(entity => entity.UpdatedStep).HasColumnName("updated_step");
        builder.HasOne<AgentWorkSession>().WithMany().HasForeignKey(entity => entity.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(entity => new
        {
            entity.SessionId,
            entity.Sequence
        }).HasDatabaseName("ix_agent_work_session_tasks_session_sequence");
        builder.HasIndex(entity => entity.ParentTaskId).HasDatabaseName("ix_agent_work_session_tasks_parent");
    }
}
