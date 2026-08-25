namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class McpServerRegistrationConfiguration : IEntityTypeConfiguration<McpServerRegistration>
{
    public void Configure(EntityTypeBuilder<McpServerRegistration> builder)
    {
        // The tier decides where a stdio server's process runs, so a value outside the enum is not a display bug — it
        // is a row the backend selector cannot resolve. Constrained in the schema for the same reason the api-key
        // scope is.
        builder.ToTable("mcp_servers",
            table => table.HasCheckConstraint("CK_mcp_servers_trust_tier", "trust_tier IN (0, 1, 2)"));
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.Name)
               .HasColumnName("name")
               // Case-insensitive collation so the unique index treats names differing only in case as duplicates,
               // matching the application-layer service's case-insensitive name handling.
               .UseCollation("NOCASE");

        builder.Property(entity => entity.Description)
               .HasColumnName("description");

        builder.Property(entity => entity.TransportKind)
               .HasColumnName("transport_kind");

        builder.Property(entity => entity.Command)
               .HasColumnName("command");

        builder.Property(entity => entity.ArgumentsJson)
               .HasColumnName("arguments");

        builder.Property(entity => entity.WorkingDirectory)
               .HasColumnName("working_directory");

        builder.Property(entity => entity.EnvJson)
               .HasColumnName("env");

        builder.Property(entity => entity.Url)
               .HasColumnName("url");

        builder.Property(entity => entity.TrustTier)
               .HasColumnName("trust_tier");

        builder.Property(entity => entity.Enabled)
               .HasColumnName("enabled");

        builder.Property(entity => entity.Version)
               .HasColumnName("version");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.UpdatedAtUtc)
               .HasColumnName("updated_at_utc");

        // The server Name is the source of the qualified tool-name slug, so uniqueness keeps tool names collision-free.
        builder.HasIndex(entity => entity.Name)
               .IsUnique();
    }
}
