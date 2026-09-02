namespace XE_Local_AI_Engine.Client.Persistence;

using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

public sealed class NodeIdentityDbContext : IdentityDbContext<NodeUser>
{
    public const string IdentityMigrationsHistoryTable = "__EFMigrationsHistory_Identity";

    public NodeIdentityDbContext(DbContextOptions<NodeIdentityDbContext> options) : base(options)
    {
    }

    public DbSet<NodeRefreshToken> RefreshTokens => Set<NodeRefreshToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        base.OnModelCreating(builder);
        ConfigureNodeUser(builder.Entity<NodeUser>());
        ConfigureRefreshToken(builder.Entity<NodeRefreshToken>());
    }

    private static void ConfigureNodeUser(EntityTypeBuilder<NodeUser> builder)
    {
        builder.Property(entity => entity.SetupCompleted)
               .HasColumnName("setup_completed");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.TutorialState)
               .HasColumnName("tutorial_state");
    }

    private static void ConfigureRefreshToken(EntityTypeBuilder<NodeRefreshToken> builder)
    {
        builder.ToTable("node_refresh_tokens");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.UserId)
               .HasColumnName("user_id")
               .IsRequired();

        builder.Property(entity => entity.TokenHash)
               .HasColumnName("token_hash")
               .HasMaxLength(128)
               .IsRequired();

        builder.Property(entity => entity.ExpiresAtUtc)
               .HasColumnName("expires_at_utc");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.RevokedAtUtc)
               .HasColumnName("revoked_at_utc");

        builder.HasOne<NodeUser>()
               .WithMany()
               .HasForeignKey(entity => entity.UserId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(entity => entity.TokenHash)
               .IsUnique();

        builder.HasIndex(entity => entity.UserId);

        builder.HasIndex(entity => entity.UserId)
               .IsUnique()
               .HasFilter("\"revoked_at_utc\" IS NULL");
    }
}
