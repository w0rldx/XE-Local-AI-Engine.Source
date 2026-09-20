namespace XE_Local_AI_Engine.Client.Models;

/// <summary>
///     A resolved node-local custom tool the bound agent's offer carries, as the runtime package needs it for the
///     session-approval memo.
/// </summary>
/// <remarks>
///     Mirrors <see cref="ResolvedSkill" />: a pure data DTO in <c>Client.Models</c> with no <c>Client.Persistence</c> dependency, so the
///     runtime package carries it without inverting the Models -&gt; Persistence direction, and that is why <see cref="IsFixed" /> is one bit
///     rather than the persistence enum, exactly as <see cref="ResolvedSkill.IsImported" /> is. ONLY a Fixed tool is ever eligible for a
///     SESSION-scoped approval — a Parameterized one is once-or-deny, because a single click must not grant open-ended, model-chosen
///     execution — and the session memo bound to <c>Version</c> mirrors <c>ApprovalMemoKey.SkillVersion</c>.
/// </remarks>
/// <param name="Version">The store's content-version, bumped on ANY content-affecting edit; the runner binds an "approve for session" memo to it, so an edit re-prompts.</param>
/// <param name="IsFixed"><c>CustomToolMode.Fixed</c>, a verbatim operator-authored invocation, rather than <c>CustomToolMode.Parameterized</c>.</param>
public sealed record ResolvedCustomTool(
    string Name,
    int Version,
    bool IsFixed);
