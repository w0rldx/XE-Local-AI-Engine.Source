namespace XE_Local_AI_Engine.Client.Services.Development;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Retention policy for the per-task Development Mode workspace clones and runtime directories (section
///     <c>Development:WorkspaceRetention</c>), which nothing else deletes.
/// </summary>
/// <remarks>
///     Only a <c>Completed</c> or <c>Cancelled</c> task's directories are reclaimed, never on the transition itself:
///     a retried or resumed task reuses its workspace (ADR 0001).
/// </remarks>
public sealed class DevelopmentWorkspaceRetentionOptions
{
    public const string SectionName = "Development:WorkspaceRetention";

    /// <summary>Whether the sweep runs at all. When <c>false</c> the background service is a clean no-op.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long a finished task's directories are kept after its last update.</summary>
    [Range(typeof(TimeSpan), "01:00:00", "3650.00:00:00")]
    public TimeSpan RetentionAge { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How long a directory with no task row, or a partial first clone, is left alone.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "1.00:00:00")]
    public TimeSpan OrphanGrace { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How often the sweep runs. A startup sweep runs before the first tick.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(6);
}
