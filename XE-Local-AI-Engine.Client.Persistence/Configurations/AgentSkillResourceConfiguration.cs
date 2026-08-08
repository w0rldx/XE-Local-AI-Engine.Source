namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class AgentSkillResourceConfiguration : IEntityTypeConfiguration<AgentSkillResource>
{
    public void Configure(EntityTypeBuilder<AgentSkillResource> builder)
    {
        builder.ToTable("agent_skill_resources");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.SkillId)
               .HasColumnName("skill_id");

        builder.Property(entity => entity.Name)
               .HasColumnName("name")
               // Case-insensitive collation so the per-skill unique index below treats two resources whose paths differ
               // only in case as the same file — a case-only duplicate would give the model two entries it cannot tell
               // apart, and the second would shadow the first on lookup.
               .UseCollation("NOCASE");

        builder.Property(entity => entity.Description)
               .HasColumnName("description");

        builder.Property(entity => entity.MediaType)
               .HasColumnName("media_type");

        builder.Property(entity => entity.Content)
               .HasColumnName("content");

        builder.Property(entity => entity.SizeBytes)
               .HasColumnName("size_bytes");

        // The name is the model's lookup key within one skill, so it is unique per skill (case-insensitive via the
        // NOCASE collation above) — not globally, since two skills may each bundle a references/FAQ.md.
        builder.HasIndex(entity => new
               {
                   entity.SkillId,
                   entity.Name
               })
               .IsUnique();

        // A resource is meaningless without its owning skill, so the FK cascades: deleting a skill removes its files.
        builder.HasOne<AgentSkill>()
               .WithMany()
               .HasForeignKey(entity => entity.SkillId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
