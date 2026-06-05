namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class ApprovedUtilityImageConfiguration : IEntityTypeConfiguration<ApprovedUtilityImage>
{
    public void Configure(EntityTypeBuilder<ApprovedUtilityImage> builder)
    {
        builder.ToTable("approved_utility_images");
        builder.HasKey(entity => entity.ApprovedImageId);

        builder.Property(entity => entity.ApprovedImageId)
               .HasColumnName("approved_image_id")
               // Case-insensitive collation so the descriptor id primary key and lookups treat ids differing only in
               // case as the same descriptor, matching the application-layer case-insensitive handling.
               .UseCollation("NOCASE");

        builder.Property(entity => entity.DisplayName)
               .HasColumnName("display_name");

        builder.Property(entity => entity.Description)
               .HasColumnName("description");

        builder.Property(entity => entity.Purpose)
               .HasColumnName("purpose");

        builder.Property(entity => entity.ImageReference)
               .HasColumnName("image_reference");

        builder.Property(entity => entity.SourceUrl)
               .HasColumnName("source_url");

        builder.Property(entity => entity.UpstreamVersion)
               .HasColumnName("upstream_version");

        builder.Property(entity => entity.Enabled)
               .HasColumnName("enabled");

        builder.Property(entity => entity.DeprecatedAtUtc)
               .HasColumnName("deprecated_at_utc");

        builder.Property(entity => entity.ReplacementApprovedImageId)
               .HasColumnName("replacement_approved_image_id");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.UpdatedAtUtc)
               .HasColumnName("updated_at_utc");

        builder.Property(entity => entity.LastUsedAtUtc)
               .HasColumnName("last_used_at_utc");

        builder.Property(entity => entity.LastSuccessfulRunAtUtc)
               .HasColumnName("last_successful_run_at_utc");

        builder.Property(entity => entity.DiagnosticsJson)
               .HasColumnName("diagnostics_json");
    }
}
