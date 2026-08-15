namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class TrainingDatasetSampleConfiguration : IEntityTypeConfiguration<TrainingDatasetSample>
{
    public void Configure(EntityTypeBuilder<TrainingDatasetSample> builder)
    {
        builder.ToTable("training_dataset_samples",
            table => table.HasCheckConstraint("CK_training_dataset_samples_sequence", "sequence >= 0"));
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.DatasetId).HasColumnName("dataset_id");
        builder.Property(entity => entity.Sequence).HasColumnName("sequence");
        builder.Property(entity => entity.Kind).HasColumnName("kind").HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.Label).HasColumnName("label").HasConversion<string>().HasMaxLength(16);
        builder.Property(entity => entity.ReviewState).HasColumnName("review_state").HasConversion<string>().HasMaxLength(16);
        builder.Property(entity => entity.ContentJson).HasColumnName("content_json").IsRequired();
        builder.Property(entity => entity.ValidationJson).HasColumnName("validation_json");
        builder.Property(entity => entity.Provenance).HasColumnName("provenance").HasConversion<string>().HasMaxLength(16);
        builder.Property(entity => entity.SourceHash).HasColumnName("source_hash").HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");

        // Samples belong to their dataset, so the declared behaviour is cascade — but the node connection never sets
        // PRAGMA foreign_keys=ON, so no cascade actually fires. Deleting a dataset means explicit ordered deletes
        // (samples first) in the store; this declaration documents ownership, it does not enforce it.
        builder.HasOne<TrainingDataset>()
               .WithMany()
               .HasForeignKey(entity => entity.DatasetId)
               .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(entity => new
        {
            entity.DatasetId,
            entity.Sequence
        }).IsUnique().HasDatabaseName("ux_training_dataset_samples_dataset_sequence");

        // Dedup is per dataset: a duplicate source hash within one dataset is skipped and counted, while the same hash
        // in another dataset is legitimate — hence scoped and non-unique.
        builder.HasIndex(entity => new
        {
            entity.DatasetId,
            entity.SourceHash
        }).HasDatabaseName("ix_training_dataset_samples_dataset_source_hash");
    }
}
