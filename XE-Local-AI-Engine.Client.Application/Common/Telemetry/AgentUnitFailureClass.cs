namespace XE_Local_AI_Engine.Client.Common.Telemetry;

using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     One closed vocabulary for "why did an agent execution unit stop", across the three units that answer the
///     question in three different alphabets: a workflow node run's <c>failure_class</c>, an
///     <c>agent_execution_logs</c> row's <c>FailureCategory</c>, and a Development attempt's <c>[code]</c>-prefixed
///     terminal reason. A cost comparison that groups by whichever alphabet a row happened to be written in is not a
///     comparison, so the grouping token is named once, here.
///     <para>
///         **Only the workflow arm is shipped as code.** <see cref="FromDevWorkflowFailureClass" /> has a real caller
///         (the run composer's node drill-down). The other two vocabularies are not loaded by any composer — one lives
///         on the execution log, the other on a Development attempt — so they ship as documented SQL <c>CASE</c>
///         fragments in <c>docs/runbooks/agent-unit-cost-telemetry-runbook.md</c>, which is where a cross-unit rollup
///         is actually run. Two mappers nothing calls would be two mappers nothing tests.
///     </para>
///     <para>
///         Nothing ROUTES on any of this. It is a reporting grouping, written onto a read model and never onto a row,
///         so a token added or re-pointed here changes a report and no run.
///     </para>
/// </summary>
public static class AgentUnitFailureClass
{
    /// <summary>An operator stopped it.</summary>
    public const string Cancelled = "Cancelled";

    /// <summary>A deadline expired.</summary>
    public const string Timeout = "Timeout";

    /// <summary>The host died under it. Neither a failure of the work nor a decision about it.</summary>
    public const string Interrupted = "Interrupted";

    /// <summary>The provider or the agent runtime could not be reached, or would not answer.</summary>
    public const string Provider = "Provider";

    /// <summary>The model cannot do what the unit needed, is not installed, or would not load.</summary>
    public const string ModelCapability = "ModelCapability";

    /// <summary>The context window or an output-token budget was exceeded.</summary>
    public const string ContextExceeded = "ContextExceeded";

    /// <summary>The unit cannot run as configured. A retry produces the same answer.</summary>
    public const string Configuration = "Configuration";

    /// <summary>A policy refused the work: a protected path, a manifest touch, an unacknowledged repository.</summary>
    public const string Policy = "Policy";

    /// <summary>A budget ran out — attempts, resumes, provider calls, tool calls.</summary>
    public const string BudgetExhausted = "BudgetExhausted";

    /// <summary>A tool call or a validation command reported failure. Often the fix loop's fuel rather than an error.</summary>
    public const string ToolOrCommand = "ToolOrCommand";

    /// <summary>A human said no.</summary>
    public const string Rejected = "Rejected";

    /// <summary>Everything else, including a reason written in no vocabulary at all.</summary>
    public const string Internal = "Internal";

    /// <summary>
    ///     The shipped arm of the table, as data rather than as a <c>switch</c>: a <c>switch</c> with a discard arm
    ///     cannot be asked which inputs it actually knows, and "every failure class this runtime can write has a
    ///     deliberate group" is the property worth a test.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> ByDevWorkflowFailureClass = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [DevWorkflowFailureClasses.Cancelled] = Cancelled,
        [DevWorkflowFailureClasses.Timeout] = Timeout,
        [DevWorkflowFailureClasses.Interrupted] = Interrupted,
        [DevWorkflowFailureClasses.ProviderError] = Provider,
        [DevWorkflowFailureClasses.Configuration] = Configuration,
        [DevWorkflowFailureClasses.Policy] = Policy,
        [DevWorkflowFailureClasses.BudgetExhausted] = BudgetExhausted,
        [DevWorkflowFailureClasses.ToolCommandFailed] = ToolOrCommand,
        [DevWorkflowFailureClasses.GateRejected] = Rejected,
        [DevWorkflowFailureClasses.Internal] = Internal
    };

    /// <summary>
    ///     The group a workflow node run's <c>failure_class</c> belongs to, or null when the row records no failure at
    ///     all — which is not the same as failing in a way nobody named.
    /// </summary>
    /// <remarks>
    ///     An unrecognised class answers <see cref="Internal" /> rather than throwing: this projects a persisted column
    ///     onto a read model, and a row written by an older build must not cost a drill-down its response.
    /// </remarks>
    public static string? FromDevWorkflowFailureClass(string? failureClass)
    {
        if (string.IsNullOrWhiteSpace(failureClass))
        {
            return null;
        }

        return ByDevWorkflowFailureClass.GetValueOrDefault(failureClass, Internal);
    }
}
