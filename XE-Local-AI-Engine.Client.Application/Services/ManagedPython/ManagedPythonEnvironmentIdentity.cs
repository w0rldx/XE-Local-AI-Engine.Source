namespace XE_Local_AI_Engine.Client.Services.ManagedPython;

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
