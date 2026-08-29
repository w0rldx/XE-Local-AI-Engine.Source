namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class DevWorkflowWorkItem
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public byte[] Request { get; set; } = [];
    public DevWorkflowWorkItemStatus Status { get; set; }

    /// <summary>
    ///     The one Dev-Mode project this work item builds in, or null. Nullable because a research- or plan-only
    ///     workflow binds no repository; a run whose graph carries repo-bound nodes against a project-less work item is
    ///     rejected at run start by the runtime, not by the schema.
    /// </summary>
    public Guid? DevelopmentProjectId { get; set; }

    public long CreatedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
    public long Version { get; set; }
}
