namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class CustomToolConfiguration : IEntityTypeConfiguration<CustomTool>
{
    public void Configure(EntityTypeBuilder<CustomTool> builder)
    {
        builder.ToTable("custom_tools");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.Name)
               .HasColumnName("name")
               // Case-insensitive collation so the unique index below treats names differing only in case as the same
               // tool, matching the application-layer service's case-insensitive uniqueness check.
               .UseCollation("NOCASE");

        builder.Property(entity => entity.Description)
               .HasColumnName("description");

        builder.Property(entity => entity.Kind)
               .HasColumnName("kind");

        builder.Property(entity => entity.Mode)
               .HasColumnName("mode");

        builder.Property(entity => entity.ParametersJson)
               .HasColumnName("parameters_json")
               .HasDefaultValue("[]");

        builder.Property(entity => entity.ConfigJson)
               .HasColumnName("config_json");

        builder.Property(entity => entity.Enabled)
               .HasColumnName("enabled")
               .HasDefaultValue(true);

        builder.Property(entity => entity.Acknowledged)
               .HasColumnName("acknowledged")
               .HasDefaultValue(false);

        builder.Property(entity => entity.Version)
               .HasColumnName("version");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.UpdatedAtUtc)
               .HasColumnName("updated_at_utc");

        // Name is the MAF tool identifier, so it is unique (case-insensitive via the NOCASE collation above).
        builder.HasIndex(entity => entity.Name)
               .IsUnique();
    }
}
