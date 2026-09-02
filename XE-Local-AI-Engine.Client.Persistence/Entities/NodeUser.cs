namespace XE_Local_AI_Engine.Client.Persistence.Entities;

using Microsoft.AspNetCore.Identity;

public sealed class NodeUser : IdentityUser
{
    public bool SetupCompleted { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    ///     Per-user onboarding tour progress, serialized as a JSON array of {key, status, atUtc} entries.
    ///     Null means "no tour seen yet" (first-run prompt). The array shape lets future tours coexist under
    ///     additional keys without another migration.
    /// </summary>
    public string? TutorialState { get; set; }
}
