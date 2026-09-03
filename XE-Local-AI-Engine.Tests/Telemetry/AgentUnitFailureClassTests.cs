namespace XE_Local_AI_Engine.Tests.Telemetry;

using System.Reflection;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The one shipped arm of the cross-unit failure vocabulary. The other two arms — <c>FailureCategory</c> on an
///     execution-log row and the <c>[code]</c> prefix on a Development attempt's terminal reason — ship as SQL in the
///     runbook, because no composer loads either vocabulary and a mapper nothing calls is a mapper nothing tests.
/// </summary>
public sealed class AgentUnitFailureClassTests
{
    /// <summary>
    ///     The guard that matters: EVERY failure class this runtime can write onto a node run has a deliberate group.
    ///     Add a constant to <c>DevWorkflowFailureClasses</c> without deciding where it belongs and this fails — which
    ///     is why the table is data rather than a <c>switch</c> with a discard arm, since a discard arm would silently
    ///     absorb it as <c>Internal</c>.
    /// </summary>
    [Test]
    public void EveryDevWorkflowFailureClass_HasAGroup()
    {
        var declared = typeof(DevWorkflowFailureClasses)
                       .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                       .Where(static field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
                       .Select(static field => (string)field.GetRawConstantValue()!)
                       .ToList();

        AssertEx.Equal(expected: 10, declared.Count, "The vocabulary is closed; a change to its size is a change to this table.");
        var unmapped = declared.Where(static failureClass => !AgentUnitFailureClass.ByDevWorkflowFailureClass.ContainsKey(failureClass)).ToList();
        AssertEx.Empty(unmapped, $"These failure classes have no cross-unit group: {string.Join(", ", unmapped)}.");
    }

    /// <summary>The table itself, row by row, exactly as the plan's section 4.5 states it.</summary>
    [Test]
    [Arguments(DevWorkflowFailureClasses.Cancelled, AgentUnitFailureClass.Cancelled)]
    [Arguments(DevWorkflowFailureClasses.Timeout, AgentUnitFailureClass.Timeout)]
    [Arguments(DevWorkflowFailureClasses.Interrupted, AgentUnitFailureClass.Interrupted)]
    [Arguments(DevWorkflowFailureClasses.ProviderError, AgentUnitFailureClass.Provider)]
    [Arguments(DevWorkflowFailureClasses.Configuration, AgentUnitFailureClass.Configuration)]
    [Arguments(DevWorkflowFailureClasses.Policy, AgentUnitFailureClass.Policy)]
    [Arguments(DevWorkflowFailureClasses.BudgetExhausted, AgentUnitFailureClass.BudgetExhausted)]
    [Arguments(DevWorkflowFailureClasses.ToolCommandFailed, AgentUnitFailureClass.ToolOrCommand)]
    [Arguments(DevWorkflowFailureClasses.GateRejected, AgentUnitFailureClass.Rejected)]
    [Arguments(DevWorkflowFailureClasses.Internal, AgentUnitFailureClass.Internal)]
    public void ADevWorkflowFailureClass_MapsToItsGroup(string failureClass, string expected) =>
        AssertEx.Equal(expected, AgentUnitFailureClass.FromDevWorkflowFailureClass(failureClass));

    /// <summary>
    ///     No failure is not a failure nobody named: a settled row with a null class must group as null, or every
    ///     succeeded node run in a report joins the <c>Internal</c> bucket.
    /// </summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public void ARowWithNoFailure_HasNoGroup(string? failureClass) =>
        AssertEx.Null(AgentUnitFailureClass.FromDevWorkflowFailureClass(failureClass));

    /// <summary>
    ///     A class written by an older or a newer build groups as <c>Internal</c> rather than throwing. This projects a
    ///     persisted column onto a read model, and a drill-down must not 500 over a token it does not recognise.
    /// </summary>
    [Test]
    public void AnUnrecognisedClass_GroupsAsInternal() =>
        AssertEx.Equal(AgentUnitFailureClass.Internal, AgentUnitFailureClass.FromDevWorkflowFailureClass("SomethingALaterBuildInvented"));

    /// <summary>
    ///     The vocabulary is closed, and its size is the number of i18n keys the client ships under
    ///     <c>pages.devWorkflows.node.failureGroup</c>. A token added here without one shows the reader a raw
    ///     identifier, so the count is pinned on both sides: this test owns the C# half, and the client's
    ///     <c>src/features/devWorkflows/I18nParity.test.ts</c> names the same twelve tokens against <c>en.json</c>
    ///     while its parity block carries them into every other locale.
    /// </summary>
    [Test]
    public void TheVocabulary_IsTwelveDistinctTokens()
    {
        var tokens = typeof(AgentUnitFailureClass)
                     .GetFields(BindingFlags.Public | BindingFlags.Static)
                     .Where(static field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
                     .Select(static field => (string)field.GetRawConstantValue()!)
                     .ToList();

        AssertEx.Equal(expected: 12, tokens.Count);
        AssertEx.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count(), "Two names for one token would split a report's buckets.");
    }
}
