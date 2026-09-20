namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     The persisted validation report.
/// </summary>
/// <remarks>
///     <see cref="CommandProfileVersion" /> is the artifact protocol version the apply and reviewer gates compare
///     against <see cref="DevelopmentValidationRunner.ProfileVersion" />, while <see cref="CommandProfileId" /> and
///     <see cref="CommandProfileDigest" /> are an independent dimension recording which commands the gate ran.
///     Adding them does not weaken the protocol check; replacing the protocol check with them would.
/// </remarks>
internal sealed record DevelopmentValidationReport(
    bool Passed,
    string BaseCommit,
    string SubjectHash,
    string ManifestHash,
    string ExpectedResultHash,
    string CommandProfileVersion,
    string CommandProfileId,
    string CommandProfileDigest,
    /// <summary>A stable <see cref="DevelopmentValidationFailureCodes" /> value when the gate failed, else null.</summary>
    string? FailureCode,
    /// <summary>Operator-facing detail for <see cref="FailureCode" />, or null when the gate passed.</summary>
    string? FailureDetail,
    IReadOnlyList<DevelopmentCommandEvidence> Commands,
    long CompletedAtUtc);

/// <summary>Stable <see cref="DevelopmentValidationReport.FailureCode" /> values, so a UI can localize them.</summary>
internal static class DevelopmentValidationFailureCodes
{
    /// <summary>Fewer command results were recorded than the profile declares validation commands.</summary>
    public const string MissingCommandEvidence = "missing_command_evidence";

    /// <summary>A validation command did not finish — it timed out or could not be launched.</summary>
    public const string CommandDidNotComplete = "command_did_not_complete";

    /// <summary>A validation command finished with a non-zero exit code.</summary>
    public const string CommandFailed = "command_failed";

    /// <summary>A test command ran but its result could not be read. This fails the gate; it never passes by default.</summary>
    public const string TestResultsUnparsed = "test_results_unparsed";

    /// <summary>A test command reported a readable result in which nothing actually ran.</summary>
    public const string NoTestsExecuted = "no_tests_executed";

    /// <summary>A test command reported failing tests.</summary>
    public const string TestsFailed = "tests_failed";

    /// <summary>
    ///     The attempt changed a file that decides what <c>restore</c> resolves. Reported before any command runs — see
    ///     <see cref="DevelopmentDependencyManifestPolicy" /> for why this is a verdict the agent can act on rather
    ///     than a security abort.
    /// </summary>
    public const string DependencyManifestChanged = "dependency_manifest_changed";
}

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

    private static readonly DevelopmentValidationVerdict Success = new() { Passed = true, FailureCode = null, FailureDetail = null };

    public static DevelopmentValidationVerdict Evaluate(DevelopmentCommandProfile profile,
        IReadOnlyList<DevelopmentCommandEvidence> commands)
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
                return failure;
            }
        }

        return Success;
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

internal sealed class DevelopmentValidationResult
{
    public required Guid ArtifactId { get; init; }

    public required bool Passed { get; init; }

    public required DevelopmentTaskStatus TaskStatus { get; init; }

    public required string SubjectHash { get; init; }
}
