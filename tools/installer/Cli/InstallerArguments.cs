namespace XE_Local_AI_Engine.Installer.Cli;

/// <summary>
///     The parsed, validated command line for a single invocation. Immutable so the orchestrator
///     and the state machine cannot mutate the operator's intent after parsing.
/// </summary>
public sealed record InstallerArguments
{
    public required InstallerVerb Verb { get; init; }

    /// <summary>Path to the unzipped RC bundle (the <c>--bundle</c> directory). Required for <c>install</c>.</summary>
    public string? BundlePath { get; init; }

    /// <summary>Skip the interactive typed confirmation on <c>remove</c>/<c>reset</c> (<c>--yes</c>).</summary>
    public bool AssumeYes { get; init; }

    /// <summary>Print the teardown inventory without deleting anything (<c>--dry-run</c>; maps to the ps1 <c>-WhatIf</c>).</summary>
    public bool DryRun { get; init; }

    /// <summary>Keep pulled models during teardown (<c>--keep-models</c>; maps to the ps1 <c>-KeepModels</c>).</summary>
    public bool KeepModels { get; init; }
}
