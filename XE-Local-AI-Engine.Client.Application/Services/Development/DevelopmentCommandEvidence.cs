namespace XE_Local_AI_Engine.Client.Services.Development;

/// <param name="TestOutcome">
///     The structured test result, or null when the command produces none; a code-owned
///     <see cref="IDevelopmentTestResultAdapter" /> reads it from the raw output before truncation
///     (<see cref="Implementation.DevelopmentWorkspaceTools.ExecuteCatalogAsync" />).
/// </param>
internal sealed record DevelopmentCommandEvidence(
    string CommandId,
    int ExitCode,
    bool Completed,
    bool OutputTruncated,
    long DurationMilliseconds,
    string StandardOutput,
    string StandardError,
    DevelopmentTestOutcome? TestOutcome = null);
