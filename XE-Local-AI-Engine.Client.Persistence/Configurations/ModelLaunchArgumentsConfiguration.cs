namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class ModelLaunchArgumentsConfiguration : IEntityTypeConfiguration<ModelLaunchArguments>
{
    public void Configure(EntityTypeBuilder<ModelLaunchArguments> builder)
    {
        builder.ToTable("model_launch_arguments");
        builder.HasKey(entity => entity.ModelName);

        builder.Property(entity => entity.ModelName)
               .HasColumnName("model_name")
               // Case-insensitive collation so the model-name primary key and lookups treat names differing only in
               // case as the same model, matching the case-insensitive model routing elsewhere in the schema.
               .UseCollation("NOCASE");

        builder.Property(entity => entity.RawArguments)
               .HasColumnName("raw_arguments");

        builder.Property(entity => entity.UpdatedAtUtc)
               .HasColumnName("updated_at_utc");
    }
}
