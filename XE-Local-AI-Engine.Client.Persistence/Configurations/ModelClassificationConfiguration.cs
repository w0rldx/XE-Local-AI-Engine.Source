namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class ModelClassificationConfiguration : IEntityTypeConfiguration<ModelClassification>
{
    public void Configure(EntityTypeBuilder<ModelClassification> builder)
    {
        builder.ToTable("model_classifications");
        builder.HasKey(entity => entity.ModelName);

        builder.Property(entity => entity.ModelName)
               .HasColumnName("model_name")
               // Case-insensitive collation so the model-name primary key and lookups treat names differing only in
               // case as the same model, matching the application-layer service's case-insensitive handling.
               .UseCollation("NOCASE");

        builder.Property(entity => entity.Digest)
               .HasColumnName("digest");

        builder.Property(entity => entity.DetectedKind)
               .HasColumnName("detected_kind")
               .HasDefaultValue(ModelKind.Unknown);

        builder.Property(entity => entity.DetectedCapabilitiesJson)
               .HasColumnName("detected_capabilities_json");

        builder.Property(entity => entity.OverrideKind)
               .HasColumnName("override_kind");

        builder.Property(entity => entity.DetectedAtUtc)
               .HasColumnName("detected_at_utc");

        builder.Property(entity => entity.UpdatedAtUtc)
               .HasColumnName("updated_at_utc");
    }
}
