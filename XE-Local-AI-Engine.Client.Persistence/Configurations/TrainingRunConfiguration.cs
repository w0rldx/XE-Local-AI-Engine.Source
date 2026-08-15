namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class TrainingRunConfiguration : IEntityTypeConfiguration<TrainingRun>
{
    public void Configure(EntityTypeBuilder<TrainingRun> builder)
    {
        builder.ToTable("training_runs",
            table => table.HasCheckConstraint("CK_training_runs_dataset_revision", "dataset_revision >= 0"));
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.DatasetId).HasColumnName("dataset_id");

        // Same width as training_datasets.content_fingerprint — "v1:" plus 64 hex characters.
        builder.Property(entity => entity.DatasetContentFingerprint).HasColumnName("dataset_content_fingerprint").HasMaxLength(67).IsRequired();
        builder.Property(entity => entity.DatasetRevision).HasColumnName("dataset_revision");
        builder.Property(entity => entity.FreezeJson).HasColumnName("freeze_json").IsRequired();
        builder.Property(entity => entity.BaseArtifactId).HasColumnName("base_artifact_id");
        builder.Property(entity => entity.LinkedInstalledModelName).HasColumnName("linked_installed_model_name").HasMaxLength(255);
        builder.Property(entity => entity.LinkedModelContentFingerprint).HasColumnName("linked_model_content_fingerprint").HasMaxLength(67);
        builder.Property(entity => entity.OptionsJson).HasColumnName("options_json").IsRequired();
        builder.Property(entity => entity.LicenseConfirmationJson).HasColumnName("license_confirmation_json");
        builder.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        builder.Property(entity => entity.ProgressJson).HasColumnName("progress_json");
        builder.Property(entity => entity.LogTail).HasColumnName("log_tail");
        builder.Property(entity => entity.LaunchReceiptJson).HasColumnName("launch_receipt_json");
        builder.Property(entity => entity.ErrorMessage).HasColumnName("error_message").HasMaxLength(1024);
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");

        // Restricted on both cross-aggregate references, like training_datasets -> training_dataset_definitions. The
        // node connection never sets PRAGMA foreign_keys=ON, so these declare the delete guard the store enforces
        // explicitly; they do not enforce it themselves.
        builder.HasOne<TrainingDataset>()
               .WithMany()
               .HasForeignKey(entity => entity.DatasetId)
               .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TrainingBaseArtifact>()
               .WithMany()
               .HasForeignKey(entity => entity.BaseArtifactId)
               .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(entity => entity.DatasetId).HasDatabaseName("ix_training_runs_dataset");
        builder.HasIndex(entity => entity.BaseArtifactId).HasDatabaseName("ix_training_runs_base_artifact");
        builder.HasIndex(entity => entity.Status).HasDatabaseName("ix_training_runs_status");
    }
}
