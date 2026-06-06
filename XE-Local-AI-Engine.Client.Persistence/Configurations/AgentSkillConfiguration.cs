namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class AgentSkillConfiguration : IEntityTypeConfiguration<AgentSkill>
{
    public void Configure(EntityTypeBuilder<AgentSkill> builder)
    {
        builder.ToTable("agent_skills");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.Name)
               .HasColumnName("name")
               // Case-insensitive collation so the unique index below treats names differing only in case as the same
               // skill, matching the application-layer service's case-insensitive uniqueness check.
               .UseCollation("NOCASE");

        builder.Property(entity => entity.Description)
               .HasColumnName("description");

        builder.Property(entity => entity.Body)
               .HasColumnName("body");

        builder.Property(entity => entity.Enabled)
               .HasColumnName("enabled")
               .HasDefaultValue(true);

        builder.Property(entity => entity.Version)
               .HasColumnName("version");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.UpdatedAtUtc)
               .HasColumnName("updated_at_utc");

        // Name is the MAF skill identifier, so it is unique (case-insensitive via the NOCASE collation above).
        builder.HasIndex(entity => entity.Name)
               .IsUnique();
    }
}
