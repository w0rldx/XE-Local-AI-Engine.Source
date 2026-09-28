namespace XE_Local_AI_Engine.Client.Services.Development;

internal sealed record DevelopmentCommandEvidence(
    string CommandId,
    int ExitCode,
    bool Completed,
    bool OutputTruncated,
    long DurationMilliseconds,
    string StandardOutput,
    string StandardError,
    /// <summary>
    ///     The structured test result for this command, or null when the command produces none. Read by a code-owned
    ///     <see cref="IDevelopmentTestResultAdapter" /> from the command's raw output before that output is truncated
    ///     for evidence — see <see cref="DevelopmentWorkspaceTools.ExecuteCatalogAsync" />.
    /// </summary>
    DevelopmentTestOutcome? TestOutcome = null);
