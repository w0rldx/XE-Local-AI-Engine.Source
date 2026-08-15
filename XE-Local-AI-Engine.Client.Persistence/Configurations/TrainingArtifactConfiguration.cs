namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class TrainingArtifactConfiguration : IEntityTypeConfiguration<TrainingArtifact>
{
    public void Configure(EntityTypeBuilder<TrainingArtifact> builder)
    {
        builder.ToTable("training_artifacts",
            table => table.HasCheckConstraint("CK_training_artifacts_size_bytes", "size_bytes >= 0"));
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.RunId).HasColumnName("run_id");
        builder.Property(entity => entity.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16);
        builder.Property(entity => entity.Path).HasColumnName("path").HasMaxLength(1024).IsRequired();
        builder.Property(entity => entity.Sha256).HasColumnName("sha256").HasMaxLength(64);
        builder.Property(entity => entity.SizeBytes).HasColumnName("size_bytes");
        builder.Property(entity => entity.SmokeState).HasColumnName("smoke_state").HasConversion<string>().HasMaxLength(16);
        builder.Property(entity => entity.SmokeReason).HasColumnName("smoke_reason").HasMaxLength(1024);
        builder.Property(entity => entity.CommittedModelName).HasColumnName("committed_model_name").HasMaxLength(255);
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");

        // Restricted rather than cascade: the staged files outlive the row until the store deletes them in order, and a
        // run whose artifacts are still staged must not vanish out from under them.
        builder.HasOne<TrainingRun>()
               .WithMany()
               .HasForeignKey(entity => entity.RunId)
               .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(entity => entity.RunId).HasDatabaseName("ix_training_artifacts_run");
    }
}
