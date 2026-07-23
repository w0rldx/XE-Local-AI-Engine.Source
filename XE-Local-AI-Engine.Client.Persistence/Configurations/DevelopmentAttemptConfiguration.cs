namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class DevelopmentAttemptConfiguration : IEntityTypeConfiguration<DevelopmentAttempt>
{
    public void Configure(EntityTypeBuilder<DevelopmentAttempt> builder)
    {
        builder.ToTable("development_attempts");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.TaskId).HasColumnName("task_id");
        builder.Property(entity => entity.PredecessorAttemptId).HasColumnName("predecessor_attempt_id");
        builder.Property(entity => entity.Role).HasColumnName("role").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.ModelId).HasColumnName("model_id").HasMaxLength(255).IsRequired();
        builder.Property(entity => entity.Provider).HasColumnName("provider").HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.StartedAtUtc).HasColumnName("started_at_utc");
        builder.Property(entity => entity.EndedAtUtc).HasColumnName("ended_at_utc");
        builder.Property(entity => entity.TerminalReason).HasColumnName("terminal_reason").HasMaxLength(1024);
        builder.Property(entity => entity.InputTokens).HasColumnName("input_tokens");
        builder.Property(entity => entity.OutputTokens).HasColumnName("output_tokens");
        builder.Property(entity => entity.StartOperationId).HasColumnName("start_operation_id");
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.HasOne<DevelopmentTask>().WithMany().HasForeignKey(entity => entity.TaskId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<DevelopmentAttempt>().WithMany().HasForeignKey(entity => entity.PredecessorAttemptId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(entity => entity.PredecessorAttemptId).HasDatabaseName("ix_development_attempts_predecessor_attempt_id");
        builder.HasIndex(entity => new
        {
            entity.TaskId,
            entity.StartedAtUtc
        }).HasDatabaseName("ix_development_attempts_task_started_at");
        builder.HasIndex(entity => entity.StartOperationId).IsUnique().HasDatabaseName("ux_development_attempts_start_operation_id");
        builder.HasIndex(entity => entity.TaskId)
               .IsUnique()
               .HasFilter("status IN ('Pending', 'Running')")
               .HasDatabaseName("ux_development_attempts_one_active_per_task");
    }
}
