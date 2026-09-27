namespace XE_Local_AI_Engine.Client.Endpoints.Python.V1;

/// <summary>What an environment was built from. The lockfile digest stays server-side; <c>mismatches</c> names a differing lockfile as <c>lockfile</c>.</summary>
public sealed class ManagedPythonEnvironmentIdentityResponse
{
    public required string PythonMinor { get; init; }
    public required int ProfileRevision { get; init; }
    public required string Rid { get; init; }
    public int? ProbeContractVersion { get; init; }
    public string? UvVersion { get; init; }
}

public sealed class ManagedPythonEnvironmentResponse
{
    public required string ProfileId { get; init; }
    public required string State { get; init; }
    public string? Reason { get; init; }
    public ManagedPythonEnvironmentIdentityResponse? Installed { get; init; }
    public required IReadOnlyList<string> Mismatches { get; init; }
}

public sealed class ManagedPythonToolchainResponse
{
    public required string UvVersion { get; init; }
    public required bool UvPresent { get; init; }
    public required IReadOnlyList<string> PythonInstalls { get; init; }
}

public sealed class ManagedPythonStatusResponse
{
    public required ManagedPythonToolchainResponse Toolchain { get; init; }
    public required IReadOnlyList<ManagedPythonEnvironmentResponse> Environments { get; init; }
}

public sealed class ManagedPythonBlockedResponse
{
    public required string Reason { get; init; }
    public required string Message { get; init; }
}
