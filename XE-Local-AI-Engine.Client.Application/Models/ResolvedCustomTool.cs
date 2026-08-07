namespace XE_Local_AI_Engine.Client.Models;

/// <summary>
///     A resolved node-local custom tool the bound agent's offer carries, as the runtime package needs it for the
///     session-approval memo. Mirrors <see cref="ResolvedSkill" />: it is a pure data DTO in <c>Client.Models</c> (no
///     <c>Client.Persistence</c> dependency), so the runtime package can carry it without inverting the
///     Models -&gt; Persistence direction.
///     <para>
///         <see cref="Version" /> is the store's content-version, bumped on ANY content-affecting edit. The invocation
///         runner binds a "approve for session" memo to it (mirroring <c>ApprovalMemoKey.SkillVersion</c>), so an edit
///         invalidates a prior grant and re-prompts.
///     </para>
///     <para>
///         <see cref="IsFixed" /> records whether the tool runs a verbatim, operator-authored invocation
///         (<c>CustomToolMode.Fixed</c>) rather than one the model parameterizes (<c>CustomToolMode.Parameterized</c>).
///         It is one bit rather than the persistence enum for the same reason <see cref="ResolvedSkill.IsImported" /> is:
///         <c>Client.Models</c> keeps no dependency on <c>Client.Persistence</c>. Its one runtime effect is that ONLY a
///         Fixed tool is ever eligible for a SESSION-scoped approval — a Parameterized tool is once-or-deny only, because
///         one click must not grant open-ended, model-chosen execution.
///     </para>
/// </summary>
public sealed record ResolvedCustomTool(
    string Name,
    int Version,
    bool IsFixed);
