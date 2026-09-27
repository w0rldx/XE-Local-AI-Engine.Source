namespace XE_Local_AI_Engine.Client.Services.ManagedPython;

/// <summary>Where one uv-managed feature environment stands (ADR 0016 §4). Reading it never provisions.</summary>
public enum ManagedPythonEnvironmentState
{
    NotProvisioned = 0,
    Provisioning = 1,
    Ready = 2,
    UpdateRequired = 3,
    RepairRequired = 4,
    Failed = 5,
    Unsupported = 6
}

/// <summary>
///     What a feature environment was built from. Two identities that differ in any compared field mean the environment
///     needs an update; <see cref="UvVersion" /> is informational only, so a uv bump never flags a working environment.
/// </summary>
public sealed class ManagedPythonEnvironmentIdentity
{
    public required string ProfileId { get; init; }

    public required string PythonMinor { get; init; }

    public required string LockfileSha256 { get; init; }

    public required int ProfileRevision { get; init; }

    public required string Rid { get; init; }

    /// <summary>The probe handshake version; Training only.</summary>
    public int? ProbeContractVersion { get; init; }

    public string? UvVersion { get; init; }

    /// <summary>The names of the compared fields that differ from <paramref name="expected" />, empty when it matches.</summary>
    public IReadOnlyList<string> MismatchesAgainst(ManagedPythonEnvironmentIdentity expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var mismatches = new List<string>();
        Compare(ProfileId, expected.ProfileId, "profileId");
        Compare(PythonMinor, expected.PythonMinor, "pythonMinor");
        Compare(LockfileSha256, expected.LockfileSha256, "lockfile");
        Compare(ProfileRevision, expected.ProfileRevision, "profileRevision");
        Compare(Rid, expected.Rid, "rid");
        Compare(ProbeContractVersion, expected.ProbeContractVersion, "probeContract");
        return mismatches;

        void Compare<T>(T actual, T wanted, string name)
        {
            if (!EqualityComparer<T>.Default.Equals(actual, wanted))
            {
                mismatches.Add(name);
            }
        }
    }
}

/// <summary>One feature environment's state, with a user-safe reason for every state but <c>Ready</c> and <c>NotProvisioned</c>.</summary>
public sealed class ManagedPythonEnvironmentStatus
{
    public required string ProfileId { get; init; }

    public required ManagedPythonEnvironmentState State { get; init; }

    public string? Reason { get; init; }

    /// <summary>What the installed environment was built from, when a readable record of it exists.</summary>
    public ManagedPythonEnvironmentIdentity? Installed { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = [];
}

/// <summary>The shared toolchain store as this host sees it: the pinned uv and the CPython installs on disk.</summary>
public sealed class ManagedPythonToolchainStatus
{
    public required string UvVersion { get; init; }

    public required bool UvPresent { get; init; }

    public required IReadOnlyList<string> PythonInstalls { get; init; }
}

public sealed class ManagedPythonStatus
{
    public required ManagedPythonToolchainStatus Toolchain { get; init; }

    public required IReadOnlyList<ManagedPythonEnvironmentStatus> Environments { get; init; }
}

public enum ManagedPythonActionOutcome
{
    Completed = 0,
    Started = 1,
    Unsupported = 2,
    Busy = 3,
    Failed = 4
}

/// <summary>The result of a Compute repair or remove; <see cref="Message" /> is user-safe and set for every refusal.</summary>
public sealed class ManagedPythonActionResult
{
    public required ManagedPythonActionOutcome Outcome { get; init; }

    public string? Message { get; init; }
}
