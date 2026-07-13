namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class ChatMaintenanceStateConfiguration : IEntityTypeConfiguration<ChatMaintenanceState>
{
    public void Configure(EntityTypeBuilder<ChatMaintenanceState> builder)
    {
        builder.ToTable("chat_maintenance_state");
        builder.HasKey(entity => entity.Name);

        builder.Property(entity => entity.Name)
               .HasColumnName("name");

        builder.Property(entity => entity.Value)
               .HasColumnName("value");
    }
}
