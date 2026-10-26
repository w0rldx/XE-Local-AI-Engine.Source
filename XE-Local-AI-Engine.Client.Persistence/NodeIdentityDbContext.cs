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

    public DbSet<NodeAuthSession> AuthSessions => Set<NodeAuthSession>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        base.OnModelCreating(builder);
        ConfigureNodeUser(builder.Entity<NodeUser>());
        ConfigureAuthSession(builder.Entity<NodeAuthSession>());
        ConfigureRefreshToken(builder.Entity<NodeRefreshToken>());
    }

    private static void ConfigureAuthSession(EntityTypeBuilder<NodeAuthSession> builder)
    {
        builder.ToTable("node_auth_sessions");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.UserId)
               .HasColumnName("user_id")
               .IsRequired();

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.LastUsedAtUtc)
               .HasColumnName("last_used_at_utc");

        builder.Property(entity => entity.ClientLabel)
               .HasColumnName("client_label")
               .HasMaxLength(NodeAuthSession.ClientLabelMaxLength);

        builder.Property(entity => entity.RevokedAtUtc)
               .HasColumnName("revoked_at_utc");

        builder.HasOne<NodeUser>()
               .WithMany()
               .HasForeignKey(entity => entity.UserId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(entity => entity.UserId);
    }

    private static void ConfigureNodeUser(EntityTypeBuilder<NodeUser> builder)
    {
        builder.Property(entity => entity.SetupCompleted)
               .HasColumnName("setup_completed");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.TutorialState)
               .HasColumnName("tutorial_state");

        builder.Property(entity => entity.AiAcknowledgedAtUtc)
               .HasColumnName("ai_acknowledged_at_utc");
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

        builder.Property(entity => entity.ReplacedByTokenId)
               .HasColumnName("replaced_by_token_id");

        builder.Property(entity => entity.SessionId)
               .HasColumnName("session_id");

        builder.HasOne<NodeUser>()
               .WithMany()
               .HasForeignKey(entity => entity.UserId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<NodeAuthSession>()
               .WithMany()
               .HasForeignKey(entity => entity.SessionId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(entity => entity.SessionId);

        builder.HasIndex(entity => entity.TokenHash)
               .IsUnique();

        // Not unique: every sign-in starts its own rotation chain, so one user holds a live token per signed-in client.
        builder.HasIndex(entity => entity.UserId);
    }
}
