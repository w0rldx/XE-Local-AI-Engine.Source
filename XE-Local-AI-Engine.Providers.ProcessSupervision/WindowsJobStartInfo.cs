namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

/// <summary>What <see cref="WindowsJobLauncher" /> starts: one command line, its working directory and its complete environment.</summary>
public sealed record WindowsJobStartInfo
{
    /// <summary>The whole command line, argv[0] first, already quoted by the caller's CommandLineToArgvW rules.</summary>
    public required string CommandLine { get; init; }

    /// <summary>The working directory, or <see langword="null" /> for the engine's own.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>The child's complete environment; nothing of the engine's is added.</summary>
    public required IReadOnlyDictionary<string, string> Environment { get; init; }
}
