namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class TranscriptSegmentConfiguration : IEntityTypeConfiguration<TranscriptSegment>
{
    public void Configure(EntityTypeBuilder<TranscriptSegment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("transcript_segments");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.SessionId).HasColumnName("session_id");
        builder.Property(entity => entity.Seq).HasColumnName("seq");
        builder.Property(entity => entity.StartMs).HasColumnName("start_ms");
        builder.Property(entity => entity.EndMs).HasColumnName("end_ms");
        builder.Property(entity => entity.Text).HasColumnName("text");
        builder.Property(entity => entity.Channel).HasColumnName("channel");
        builder.Property(entity => entity.Confidence).HasColumnName("confidence");

        builder.HasOne<TranscriptionSession>()
            .WithMany(session => session.Segments)
            .HasForeignKey(entity => entity.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(entity => new
        {
            entity.SessionId,
            entity.Seq
        }).IsUnique().HasDatabaseName("ux_transcript_segments_session_seq");
    }
}
