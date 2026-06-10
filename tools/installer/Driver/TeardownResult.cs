namespace XE_Local_AI_Engine.Installer.Driver;

/// <summary>
///     The post-teardown attestation (plan §7.5 reset re-entry, MED-6). After <c>reset</c> runs
///     teardown it ASSERTs completeness via these flags; any residual aborts the reinstall.
/// </summary>
public sealed record TeardownResult
{
    public required bool DistroRemoved { get; init; }

    public required bool ProgramDataRemoved { get; init; }

    public required bool ManifestRemoved { get; init; }

    /// <summary>Any artifact the teardown could not remove (echoed in the abort message).</summary>
    public IReadOnlyList<string> Residuals { get; init; } = [];

    public bool IsComplete => DistroRemoved && ProgramDataRemoved && ManifestRemoved && Residuals.Count == 0;
}
