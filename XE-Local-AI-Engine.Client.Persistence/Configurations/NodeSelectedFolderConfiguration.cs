namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class NodeSelectedFolderConfiguration : IEntityTypeConfiguration<NodeSelectedFolder>
{
    public void Configure(EntityTypeBuilder<NodeSelectedFolder> builder)
    {
        builder.ToTable("selected_folders");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.Alias)
               .HasColumnName("alias");

        builder.Property(entity => entity.HostPath)
               .HasColumnName("host_path");

        builder.Property(entity => entity.Mode)
               .HasColumnName("mode")
               .HasDefaultValue(SelectedFolderMode.Copy);

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.HasIndex(entity => entity.Alias)
               .IsUnique();
    }
}
