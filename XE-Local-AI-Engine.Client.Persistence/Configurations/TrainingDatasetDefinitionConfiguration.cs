namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class TrainingDatasetDefinitionConfiguration : IEntityTypeConfiguration<TrainingDatasetDefinition>
{
    public void Configure(EntityTypeBuilder<TrainingDatasetDefinition> builder)
    {
        builder.ToTable("training_dataset_definitions",
            table => table.HasCheckConstraint("CK_training_dataset_definitions_version", "definition_version > 0"));
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(entity => entity.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.DefinitionJson).HasColumnName("definition_json").IsRequired();
        builder.Property(entity => entity.DefinitionVersion).HasColumnName("definition_version");
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");
    }
}
