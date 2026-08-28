namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Configuration for development workflows.
///     <para>
///         <see cref="Enabled" /> gates <em>behaviour</em>, never registration — the same posture work sessions hold:
///         a disabled node has to answer legibly rather than 500 out of an empty container.
///     </para>
/// </summary>
public sealed class DevWorkflowOptions
{
    public const string Section = "DevWorkflows";

    public bool Enabled { get; init; }

    /// <summary>The cap on one workflow artifact's bytes, enforced by the blob store.</summary>
    [Range(1, 64 * 1024 * 1024)]
    public int MaxArtifactBytes { get; init; } = 1024 * 1024;
}
