namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class BenchmarkProjectConfiguration : IEntityTypeConfiguration<BenchmarkProject>
{
    public void Configure(EntityTypeBuilder<BenchmarkProject> builder)
    {
        builder.ToTable("benchmark_projects", table =>
        {
            table.HasCheckConstraint("CK_benchmark_projects_context_tokens", "context_tokens > 0");
            table.HasCheckConstraint("CK_benchmark_projects_judge_context_tokens", "judge_context_tokens IS NULL OR judge_context_tokens > 0");
        });
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(entity => entity.CoreTaskJson).HasColumnName("core_task_json").IsRequired();
        builder.Property(entity => entity.ContextTokens).HasColumnName("context_tokens");
        builder.Property(entity => entity.AgentDefinitionId).HasColumnName("agent_definition_id");
        builder.Property(entity => entity.JudgeEnabled).HasColumnName("judge_enabled");
        builder.Property(entity => entity.JudgeModelName).HasColumnName("judge_model_name").HasMaxLength(255).UseCollation("NOCASE");
        builder.Property(entity => entity.JudgeContextTokens).HasColumnName("judge_context_tokens");
        builder.Property(entity => entity.JudgePromptVersion).HasColumnName("judge_prompt_version");
        builder.Property(entity => entity.JudgeOutputSchemaVersion).HasColumnName("judge_output_schema_version");
        builder.Property(entity => entity.CurrentJudgePolicyRevisionId).HasColumnName("current_judge_policy_revision_id");
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.HasIndex(entity => entity.AgentDefinitionId).HasDatabaseName("ix_benchmark_projects_agent_definition_id");
    }
}
