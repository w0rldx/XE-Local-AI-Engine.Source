namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Services.Development.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     The deterministic gate's verdict over one attempt's command evidence.
/// </summary>
/// <remarks>
///     Exit codes alone are not sufficient: a test command can exit non-zero for reasons unrelated to tests and, the
///     case that matters, a suite reduced to zero tests can exit zero. The verdict adds two rules over the exit
///     codes, taken from the structured result a code-owned adapter read — executed above zero, failed at zero — and
///     treats a result the adapter could not read as a failure, never a pass, because unreadable is exactly the
///     state an agent optimizing for green would produce if it meant "assume fine".
/// </remarks>
internal sealed class DevelopmentValidationVerdict
{
    public required bool Passed { get; init; }

    public required string? FailureCode { get; init; }

    public required string? FailureDetail { get; init; }

    /// <summary>Whether the failure is the host's rather than the patch's, so another coder round would fail identically.</summary>
    public bool StopsTask => FailureCode == DevelopmentValidationFailureCodes.RunnerNotInstalled;

    private static readonly DevelopmentValidationVerdict Success = new()
    {
        Passed = true,
        FailureCode = null,
        FailureDetail = null
    };

    /// <param name="profile">The profile whose commands ran.</param>
    /// <param name="commands">Their evidence, in order.</param>
    /// <param name="unreachable">The toolchain trees the boundary left out (<see cref="DevelopmentWorkspaceSession.UnreachableToolchain" />).</param>
    public static DevelopmentValidationVerdict Evaluate(DevelopmentCommandProfile profile,
        IReadOnlyList<DevelopmentCommandEvidence> commands,
        IReadOnlyList<DevelopmentSkippedToolchainTree>? unreachable = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(commands);

        if (commands.Count != profile.ValidationCommandIds.Count)
        {
            return new DevelopmentValidationVerdict
            {
                Passed = false,
                FailureCode = DevelopmentValidationFailureCodes.MissingCommandEvidence,
                FailureDetail = $"The profile declares {profile.ValidationCommandIds.Count} validation commands but {commands.Count} produced evidence."
            };
        }

        foreach (var command in commands)
        {
            var failure = EvaluateCommand(command);
            if (failure is not null)
            {
                return Unreachable(profile, command, failure, unreachable ?? []) ?? failure;
            }
        }

        return Success;
    }

    /// <summary>
    ///     A missing runner whose executable the host <c>PATH</c> does have, in a tree the boundary left out: never "not installed", but
    ///     unreachable, naming each tree and the rule that refused it.
    /// </summary>
    private static DevelopmentValidationVerdict? Unreachable(DevelopmentCommandProfile profile,
        DevelopmentCommandEvidence command,
        DevelopmentValidationVerdict failure,
        IReadOnlyList<DevelopmentSkippedToolchainTree> unreachable)
    {
        var executable = profile.Commands.FirstOrDefault(candidate => string.Equals(candidate.CommandId, command.CommandId, StringComparison.Ordinal))?.Executable;
        var skipped = unreachable.Where(entry => string.Equals(entry.Executable, executable, StringComparison.Ordinal)).ToList();
        if (failure.FailureCode != DevelopmentValidationFailureCodes.RunnerNotInstalled || skipped.Count == 0)
        {
            return null;
        }

        return new DevelopmentValidationVerdict
        {
            Passed = false,
            FailureCode = DevelopmentValidationFailureCodes.RunnerNotInstalled,
            FailureDetail = $"Command {command.CommandId} could not reach its runner behind the filesystem boundary. {executable} may well be installed on the host, "
                            + "but the boundary left out what the host PATH finds it through: "
                            + string.Join("; ",
                                skipped.Select(static entry =>
                                    $"'{DevelopmentArtifactSanitizer.NeutralizeEngineText(entry.Tree)}' ({DevelopmentArtifactSanitizer.NeutralizeEngineText(entry.Reason)})"))
                            + " (the engine log names each path in full). Install it somewhere the boundary can bind (owned by your account or root, not group- or world-writable, outside /tmp and the protected folders), then retry."
        };
    }

    /// <summary>
    ///     The per-command rules, in the order that yields the most useful reason rather than the earliest one.
    /// </summary>
    /// <remarks>
    ///     A failing test makes its command exit non-zero too, so checking the exit code first reports every red
    ///     suite as the generic "command failed" and throws away the specific answer the adapter just produced.
    /// </remarks>
    private static DevelopmentValidationVerdict? EvaluateCommand(DevelopmentCommandEvidence command)
    {
        // Ahead of "did not finish": the operator needs to hear what to install. The adapter's hint is the specific
        // answer; the launch-failure prefix covers every profile, including those whose adapter does not look.
        if (command.TestOutcome is { ParseFailureCode: DevelopmentTestParseFailureCodes.RunnerNotInstalled } missing)
        {
            return new DevelopmentValidationVerdict
            {
                Passed = false,
                FailureCode = DevelopmentValidationFailureCodes.RunnerNotInstalled,
                FailureDetail = $"Command {command.CommandId} could not run its tests: {missing.ParseFailureDetail}"
            };
        }

        if (command.StandardError.StartsWith(SandboxCommandResult.LaunchFailurePrefix, StringComparison.Ordinal))
        {
            return new DevelopmentValidationVerdict
            {
                Passed = false,
                FailureCode = DevelopmentValidationFailureCodes.RunnerNotInstalled,
                FailureDetail = $"Command {command.CommandId} could not be started on this host: its executable is not installed or not on the node's PATH, "
                                + "or the sandbox could not establish its filesystem boundary (the engine log says which). Install it on the host yourself; XE never installs it."
            };
        }

        if (!command.Completed)
        {
            return new DevelopmentValidationVerdict
            {
                Passed = false,
                FailureCode = DevelopmentValidationFailureCodes.CommandDidNotComplete,
                FailureDetail = $"Command {command.CommandId} did not finish."
            };
        }

        if (command.TestOutcome is { } outcome)
        {
            if (!outcome.Parsed)
            {
                return new DevelopmentValidationVerdict
                {
                    Passed = false,
                    FailureCode = DevelopmentValidationFailureCodes.TestResultsUnparsed,
                    FailureDetail = $"Command {command.CommandId} produced no readable test result ({outcome.ParseFailureCode}): {outcome.ParseFailureDetail}"
                };
            }

            if (outcome.Failed > 0)
            {
                return new DevelopmentValidationVerdict
                {
                    Passed = false,
                    FailureCode = DevelopmentValidationFailureCodes.TestsFailed,
                    FailureDetail = $"Command {command.CommandId} reported {outcome.Failed} failing of {outcome.Executed} executed tests."
                };
            }

            if (outcome.Executed == 0)
            {
                return new DevelopmentValidationVerdict
                {
                    Passed = false,
                    FailureCode = DevelopmentValidationFailureCodes.NoTestsExecuted,
                    FailureDetail = $"Command {command.CommandId} executed no tests ({outcome.Discovered} discovered). A change cannot be validated by a suite that ran nothing."
                };
            }
        }

        return command.ExitCode == 0
            ? null
            : new DevelopmentValidationVerdict
            {
                Passed = false,
                FailureCode = DevelopmentValidationFailureCodes.CommandFailed,
                FailureDetail = $"Command {command.CommandId} exited with code {command.ExitCode}."
            };
    }
}
