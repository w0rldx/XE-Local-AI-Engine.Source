namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class ModelProviderMapConfiguration : IEntityTypeConfiguration<ModelProviderMap>
{
    public void Configure(EntityTypeBuilder<ModelProviderMap> builder)
    {
        builder.ToTable("model_provider_map");
        builder.HasKey(entity => entity.ModelName);

        builder.Property(entity => entity.ModelName)
               .HasColumnName("model_name")
               // Case-insensitive collation so the model-name primary key and lookups treat names differing only in
               // case as the same model, matching the case-insensitive provider routing in the application layer.
               .UseCollation("NOCASE");

        builder.Property(entity => entity.ProviderName)
               .HasColumnName("provider_name");

        builder.Property(entity => entity.UpdatedAtUtc)
               .HasColumnName("updated_at_utc");
    }
}
