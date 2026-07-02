namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class ImageModelProfileConfiguration : IEntityTypeConfiguration<ImageModelProfile>
{
    public void Configure(EntityTypeBuilder<ImageModelProfile> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("image_model_profiles");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.MachineKey).HasColumnName("machine_key");
        builder.Property(entity => entity.ModelName).HasColumnName("model_name");
        builder.Property(entity => entity.Backend).HasColumnName("backend");
        builder.Property(entity => entity.DefaultSteps).HasColumnName("default_steps");
        builder.Property(entity => entity.DefaultSampler).HasColumnName("default_sampler");
        builder.Property(entity => entity.DefaultCfg).HasColumnName("default_cfg");
        builder.Property(entity => entity.DefaultWidth).HasColumnName("default_width");
        builder.Property(entity => entity.DefaultHeight).HasColumnName("default_height");
        builder.Property(entity => entity.Status).HasColumnName("status");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");

        // Composite natural key (machine_key, model_name, backend) — unique.
        builder.HasIndex(entity => new
               {
                   entity.MachineKey,
                   entity.ModelName,
                   entity.Backend
               })
               .IsUnique();

        builder.HasIndex(entity => entity.Status);
    }
}
