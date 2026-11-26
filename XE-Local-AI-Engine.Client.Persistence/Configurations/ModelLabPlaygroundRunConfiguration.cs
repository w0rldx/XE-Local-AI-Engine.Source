namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class ModelLabPlaygroundRunConfiguration : IEntityTypeConfiguration<ModelLabPlaygroundRun>
{
    public void Configure(EntityTypeBuilder<ModelLabPlaygroundRun> builder)
    {
        builder.ToTable("model_lab_playground_runs");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.TemplateId).HasColumnName("template_id").HasMaxLength(32).IsRequired();
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.TraceBlobId).HasColumnName("trace_blob_id");
        builder.Property(entity => entity.TraceSha256).HasColumnName("trace_sha256").HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.TraceByteCount).HasColumnName("trace_byte_count");
        builder.Property(entity => entity.ResultJson).HasColumnName("result_json").IsRequired();
        builder.HasIndex(entity => entity.CreatedAtUtc).HasDatabaseName("ix_model_lab_playground_runs_created");
    }
}
