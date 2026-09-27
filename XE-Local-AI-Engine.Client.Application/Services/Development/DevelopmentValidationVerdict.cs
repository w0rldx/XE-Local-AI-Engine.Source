namespace XE_Local_AI_Engine.Client.Services.Development;

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

    private static readonly DevelopmentValidationVerdict Success = new()
    {
        Passed = true,
        FailureCode = null,
        FailureDetail = null
    };

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
