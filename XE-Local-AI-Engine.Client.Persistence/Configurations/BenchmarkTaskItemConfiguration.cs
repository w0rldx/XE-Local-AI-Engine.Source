namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed class BenchmarkTaskItemConfiguration : IEntityTypeConfiguration<BenchmarkTaskItem>
{
    public void Configure(EntityTypeBuilder<BenchmarkTaskItem> builder)
    {
        builder.ToTable("benchmark_task_items", table =>
        {
            // The whole kind vocabulary, including the generator kinds no writer produces yet: a CHECK that has to be
            // rewritten to admit a new kind is a SQLite table rebuild, and admitting the vocabulary up front costs
            // nothing while the store is the thing that decides what may actually be written.
            table.HasCheckConstraint("CK_benchmark_task_items_kind",
                $"kind IN ('{BenchmarkTaskItemKinds.Prompt}', '{BenchmarkTaskItemKinds.Niah}', '{BenchmarkTaskItemKinds.NiahCase}')");
            table.HasCheckConstraint("CK_benchmark_task_items_index", "\"index\" >= 0");
            table.HasCheckConstraint("CK_benchmark_task_items_revision", "revision >= 1");
        });
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.ProjectId).HasColumnName("project_id");
        builder.Property(entity => entity.ParentItemId).HasColumnName("parent_item_id");
        builder.Property(entity => entity.Index).HasColumnName("index");
        builder.Property(entity => entity.Kind).HasColumnName("kind").HasMaxLength(16).IsRequired();
        builder.Property(entity => entity.Revision).HasColumnName("revision");

        // Plaintext, and that is the point: the ranking read compares a run's frozen copy against this column in the
        // same flat-column scan that never decrypts a payload.
        builder.Property(entity => entity.InputHash).HasColumnName("input_hash").HasMaxLength(67).IsRequired();
        builder.Property(entity => entity.CountsTowardScore).HasColumnName("counts_toward_score").HasDefaultValue(true);
        builder.Property(entity => entity.PromptJson).HasColumnName("prompt_json").IsRequired();
        builder.Property(entity => entity.ReferenceAnswerJson).HasColumnName("reference_answer_json");
        builder.Property(entity => entity.VerifierConfigJson).HasColumnName("verifier_config_json");
        builder.Property(entity => entity.GeneratorConfigJson).HasColumnName("generator_config_json");
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.HasOne<BenchmarkProject>().WithMany().HasForeignKey(entity => entity.ProjectId).OnDelete(DeleteBehavior.Restrict);

        // The unique index is what makes the legacy item-0 backfill idempotent under a concurrent reader: a race is a
        // constraint violation the store catches and re-reads, not a duplicate item nobody notices.
        builder.HasIndex(entity => new
        {
            entity.ProjectId,
            entity.Index
        }).IsUnique().HasDatabaseName("ux_benchmark_task_items_project_index");
        builder.HasIndex(entity => entity.ParentItemId).HasDatabaseName("ix_benchmark_task_items_parent");
    }
}
