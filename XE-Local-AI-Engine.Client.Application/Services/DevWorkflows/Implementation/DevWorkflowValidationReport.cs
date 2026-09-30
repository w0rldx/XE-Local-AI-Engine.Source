namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using XE_Local_AI_Engine.Client.Services.Development;

/// <summary>The report a Tool node-run leaves: what ran, against which commit and profile, and the gate's verdict.</summary>
/// <remarks>
///     Deliberately NOT <c>DevelopmentValidationReport</c>: that record's subject, manifest and expected-result
///     hashes describe a coder attempt's patch, and filling three hash fields with placeholders would be a report
///     claiming evidence it does not have.
/// </remarks>
internal sealed record DevWorkflowValidationReport(
    bool Passed,
    string NodeKey,
    int Attempt,
    string BaseCommit,
    string CommandProfileId,
    string CommandProfileDigest,
    string? FailureCode,
    string? FailureDetail,
    IReadOnlyList<DevelopmentCommandEvidence> Commands,
    long CompletedAtUtc,
    DevWorkflowValidationBasedOn? BasedOn = null);
