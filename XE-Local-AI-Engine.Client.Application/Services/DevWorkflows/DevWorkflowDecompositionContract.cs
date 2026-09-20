namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     What a decomposing node is told about the coder its tasks become, in code rather than in a seeded string.
/// </summary>
/// <remarks>
///     Not planning preferences but the implementation lane's own contract: a Development attempt has to export a
///     NON-EMPTY patch to finish, and is refused outright for touching a test file that existed at the base commit. A
///     decomposition that knows neither writes slices nobody can complete. Owned here rather than in the seeded
///     template because it describes the LANE: a copy per string would drift from the code enforcing it. See
///     docs/wiki/25-dev-workflows.md ("The decomposition contract").
/// </remarks>
internal static class DevWorkflowDecompositionContract
{
    /// <summary>
    ///     Appended to a node's objective whenever its materialization template carries a <c>DevTask</c> anywhere in
    ///     the subtree, straight after its own instructions and before anything the operator configured.
    /// </summary>
    /// <remarks>
    ///     The same standing as the instructions, because a task written against the wrong capabilities is worse than
    ///     one written against no policy. Not on every materializing node: a template of Agent and Tool nodes produces
    ///     no coder attempt. <c>DevWorkflowGraph.TemplateSubtreeHasDevTask</c> is the predicate, the same one the
    ///     materializer refuses a task package by, so what a decomposition is told matches what it is judged by.
    /// </remarks>
    internal const string Text = """
                                 ## What each task becomes
                                 Each task you emit becomes ONE bounded Development coder attempt on a fresh clone of the base commit. That coder has no shell and no operator to ask: it can read and edit workspace files, run only the fixed per-project command ids it is offered, and must finish by submitting a NON-EMPTY code change. A task with nothing to change — reading, surveying, profiling, capturing conventions, verifying, reviewing — cannot be completed: it fails three times and blocks the run. Never emit one. Fold the reading into the task that needs it.
                                 The coder may ADD new test files, but may never modify, delete or rename a test file that already exists at the base commit; that attempt is refused automatically. So a task that adds tests must say "in a NEW test file" and name the file.
                                 Prefer the smallest number of tasks. A request that names one method, one file or one behaviour is ONE task that implements it and adds its test file together. Split only when the slices are independent features a build can judge separately. Every task must list, in "changes", the workspace-relative files it will add or edit — at least one.
                                 """;
}
