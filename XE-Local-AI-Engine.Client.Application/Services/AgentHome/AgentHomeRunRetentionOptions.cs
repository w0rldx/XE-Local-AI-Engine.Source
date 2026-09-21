namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     Retention policy for the on-disk AgentHome run directories (section <c>AgentHome:RunRetention</c>), which are
///     append-only and kept forever without a sweep.
/// </summary>
/// <remarks>
///     Three independent limits apply on every sweep, oldest run first: age, run count, and total bytes. Each is
///     disabled on its own by setting it to zero; <see cref="Enabled" /> turns the whole sweep off.
/// </remarks>
public sealed class AgentHomeRunRetentionOptions
{
    public const string SectionName = "AgentHome:RunRetention";

    /// <summary>Whether the retention sweep runs at all. When <c>false</c> the background service is a clean no-op.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Runs older than this many days are deleted. <c>0</c> disables the age limit.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Runs beyond this many newest ones are deleted. <c>0</c> disables the count limit.</summary>
    public int MaxRuns { get; set; } = 200;

    /// <summary>
    ///     Total byte ceiling for the runs directory; oldest runs are deleted until it fits. <c>0</c> disables the
    ///     byte limit. Defaults to 2 GiB, which one exported patch at the default <c>MaxPatchBytes</c> cannot reach
    ///     alone.
    /// </summary>
    public long MaxTotalBytes { get; set; } = 2147483648;

    /// <summary>How often the sweep runs. A startup sweep runs before the first tick.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(6);
}
