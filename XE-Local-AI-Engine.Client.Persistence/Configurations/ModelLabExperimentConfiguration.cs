namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class ModelLabExperimentConfiguration : IEntityTypeConfiguration<ModelLabExperiment>
{
    public void Configure(EntityTypeBuilder<ModelLabExperiment> builder)
    {
        builder.ToTable("model_lab_experiments");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        builder.Property(entity => entity.RepoId).HasColumnName("repo_id").HasMaxLength(255).IsRequired();
        builder.Property(entity => entity.Revision).HasColumnName("revision").HasMaxLength(100).IsRequired();
        builder.Property(entity => entity.BaseArtifactId).HasColumnName("base_artifact_id");
        builder.Property(entity => entity.InstalledModelName).HasColumnName("installed_model_name").HasMaxLength(255).IsRequired();
        builder.Property(entity => entity.Prompt).HasColumnName("prompt").IsRequired();
        builder.Property(entity => entity.LicenseConfirmationJson).HasColumnName("license_confirmation_json").IsRequired();
        builder.Property(entity => entity.ResultJson).HasColumnName("result_json");
        builder.Property(entity => entity.ErrorMessage).HasColumnName("error_message");
        builder.Property(entity => entity.LaunchReceiptJson).HasColumnName("launch_receipt_json");
        builder.Property(entity => entity.TraceBlobId).HasColumnName("trace_blob_id");
        builder.Property(entity => entity.TraceSha256).HasColumnName("trace_sha256").HasMaxLength(64);
        builder.Property(entity => entity.TraceByteCount).HasColumnName("trace_byte_count");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.HasIndex(entity => entity.Status).HasDatabaseName("ix_model_lab_experiments_status");
        builder.HasIndex(entity => entity.CreatedAtUtc).HasDatabaseName("ix_model_lab_experiments_created");
    }
}
