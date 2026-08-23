namespace XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Wall-clock budgets for waits that must survive a contended CI runner.
/// </summary>
/// <remarks>
///     <para>
///         CI runs this module as four concurrent group processes with coverage instrumentation on a four-core
///         runner (see the TEST_GROUPS note in scripts/run-tests-memory-safe.sh), so a wait that is comfortable
///         on an idle developer box can be starved for seconds at a time. Budgets sized against local timings
///         are what turn that starvation into a red build on work that changed nothing.
///     </para>
///     <para>
///         These are failure deadlines, not sleeps: every consumer polls or awaits a completion and returns the
///         moment the condition holds, so a generous budget costs nothing on a green run and only decides how
///         long a genuinely stuck test waits before reporting.
///     </para>
/// </remarks>
internal static class TestBudgets
{
    /// <summary>
    ///     Deadline for an asynchronous condition that is expected within milliseconds when the machine is idle.
    /// </summary>
    public static readonly TimeSpan Contended = TimeSpan.FromSeconds(120);
}
