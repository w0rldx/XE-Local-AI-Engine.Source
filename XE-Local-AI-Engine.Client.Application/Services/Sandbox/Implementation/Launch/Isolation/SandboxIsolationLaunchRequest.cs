namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

/// <summary>The per-command inputs to <see cref="SandboxIsolationLaunch.Create" />.</summary>
internal sealed record SandboxIsolationLaunchRequest
{
    /// <summary>The sandbox's jail directory, which becomes <c>/work</c> inside.</summary>
    public required string JailRoot { get; init; }

    public required string Executable { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>Host trees bound read-only at their own canonical paths.</summary>
    public IReadOnlyList<string> ReadOnlyTrees { get; init; } = [];

    public SandboxResourceLimits? ResourceLimits { get; init; }

    public int ThreadLimit { get; init; } = 1;

    /// <summary>Wall-clock ceiling the user manager enforces on the scope.</summary>
    public required long RuntimeMaxSeconds { get; init; }

    /// <summary>The role segment of the unit name, for readability in <c>systemctl</c> output.</summary>
    public string? Role { get; init; }

    /// <summary>The command's working directory inside the sandbox; <c>/work</c> or a path beneath it.</summary>
    public string WorkingDirectory { get; init; } = SandboxIsolatedChain.WorkPath;

    /// <summary>Caller-supplied environment, emitted after the chain's own allow-list so it overrides it.</summary>
    public IReadOnlyDictionary<string, string> AdditionalEnvironment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
