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

internal sealed class DevelopmentValidationResult
{
    public required Guid ArtifactId { get; init; }

    public required bool Passed { get; init; }

    public required DevelopmentTaskStatus TaskStatus { get; init; }

    public required string SubjectHash { get; init; }
}
