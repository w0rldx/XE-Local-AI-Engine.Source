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
