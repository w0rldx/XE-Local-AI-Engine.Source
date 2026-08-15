namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class TrainingDatasetConfiguration : IEntityTypeConfiguration<TrainingDataset>
{
    public void Configure(EntityTypeBuilder<TrainingDataset> builder)
    {
        builder.ToTable("training_datasets",
            table => table.HasCheckConstraint("CK_training_datasets_counts",
                "total_sample_count >= 0 AND good_sample_count >= 0 AND bad_sample_count >= 0 AND rejected_sample_count >= 0 AND duplicate_sample_count >= 0"));
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.DefinitionId).HasColumnName("definition_id");
        builder.Property(entity => entity.DefinitionVersion).HasColumnName("definition_version");
        builder.Property(entity => entity.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        builder.Property(entity => entity.Revision).HasColumnName("revision");

        // Same width as benchmark_runs.model_content_fingerprint — "v1:" plus 64 hex characters.
        builder.Property(entity => entity.ContentFingerprint).HasColumnName("content_fingerprint").HasMaxLength(67);
        builder.Property(entity => entity.TotalSampleCount).HasColumnName("total_sample_count");
        builder.Property(entity => entity.GoodSampleCount).HasColumnName("good_sample_count");
        builder.Property(entity => entity.BadSampleCount).HasColumnName("bad_sample_count");
        builder.Property(entity => entity.RejectedSampleCount).HasColumnName("rejected_sample_count");
        builder.Property(entity => entity.DuplicateSampleCount).HasColumnName("duplicate_sample_count");
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");

        // Restricted, like benchmark_runs -> benchmark_projects: a definition with datasets is deleted only after its
        // datasets are, and the store issues those deletes explicitly (see TrainingDatasetSampleConfiguration).
        builder.HasOne<TrainingDatasetDefinition>()
               .WithMany()
               .HasForeignKey(entity => entity.DefinitionId)
               .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(entity => new
        {
            entity.DefinitionId,
            entity.CreatedAtUtc
        }).HasDatabaseName("ix_training_datasets_definition_created_at");
    }
}
