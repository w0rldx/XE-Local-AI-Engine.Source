namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     A consumed-by edge, captured at node start. This is the mechanism of mark-only staleness: when a lineage gains a
///     new version, every node-run that recorded a use of an earlier version has its own artifacts flagged. The row
///     points at the exact <em>version</em> consumed, which is what makes "consumed v1, v2 exists" decidable.
/// </summary>
internal sealed class DevWorkflowArtifactUse
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid NodeRunId { get; set; }
    public Guid ArtifactId { get; set; }
    public long RecordedSequence { get; set; }
}
