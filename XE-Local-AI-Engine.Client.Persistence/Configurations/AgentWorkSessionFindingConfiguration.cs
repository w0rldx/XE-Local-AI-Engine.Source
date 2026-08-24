namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class AgentWorkSessionFindingConfiguration : IEntityTypeConfiguration<AgentWorkSessionFinding>
{
    public void Configure(EntityTypeBuilder<AgentWorkSessionFinding> builder)
    {
        builder.ToTable("agent_work_session_findings");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.SessionId).HasColumnName("session_id");
        builder.Property(entity => entity.TaskId).HasColumnName("task_id");
        builder.Property(entity => entity.Sequence).HasColumnName("sequence");
        builder.Property(entity => entity.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.Text).HasColumnName("text").IsRequired();
        builder.Property(entity => entity.SourceRef).HasColumnName("source_ref");
        builder.Property(entity => entity.CreatedStep).HasColumnName("created_step");
        builder.Property(entity => entity.Superseded).HasColumnName("superseded");
        builder.HasOne<AgentWorkSession>().WithMany().HasForeignKey(entity => entity.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AgentWorkSessionTask>().WithMany().HasForeignKey(entity => entity.TaskId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(entity => new
        {
            entity.SessionId,
            entity.Sequence
        }).HasDatabaseName("ix_agent_work_session_findings_session_sequence");
        builder.HasIndex(entity => new
        {
            entity.SessionId,
            entity.Kind,
            entity.Superseded
        }).HasDatabaseName("ix_agent_work_session_findings_session_kind_superseded");
    }
}
