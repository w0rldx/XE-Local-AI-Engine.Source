import { z } from "zod";

// Manual playbook actions. Each action carries an injected behavior (the instruction text appended
// to the owning agent's system prompt when its playbook is enabled), an enable/disable state, a provenance
// source (P1 writes "Manual" only — the analysis-proposed "Analysis" source arrives in a later phase), an
// optional trigger condition + scope tag (advisory/display in P1), and a Priority that orders injection.
//
// Mirrors the backend PlaybookActionState (Suggested=0, Enabled=1, Disabled=2, Archived=3) and
// PlaybookActionSource (Manual=0, Analysis=1) enums; the wire contract carries the string form. P1 only ever
// shows/authors Enabled|Disabled state and Manual source — the other enum slots are reserved for later phases.
//
// Boundary validation + wire→domain mapping live in PlaybookActionMappers.ts (the generated zod validator owns the
// response shape). This module is the domain view-models + the create/edit FORM schemas only.

export type PlaybookActionState = "Suggested" | "Enabled" | "Disabled" | "Archived";

export type PlaybookActionSource = "Manual" | "Analysis";

// Eval gate. How a single golden case was scored: a deterministic required/forbidden-phrase
// assertion or a node-local LLM judge against the case rubric.
export type EvalScoredBy = "assertion" | "judge";

// One golden case's baseline-vs-candidate outcome. `regressed` is `baselinePass && !candidatePass` (a prior-good
// case the candidate action broke) — the single criterion the promote gate blocks on.
export interface EvalCase {
	readonly goldenCaseId: string;
	readonly scoredBy: EvalScoredBy;
	readonly baselinePass: boolean;
	readonly candidatePass: boolean;
	readonly regressed: boolean;
}

// The regression-gate outcome recorded on a Suggested action before it may be promoted. Ids +
// pass/fail flags + counts only (no transcripts). `passed` is `goldenCaseCount > 0 && regressedCaseCount === 0`;
// an empty golden set never passes (you cannot prove no-regression with zero cases). Null until an eval runs;
// cleared when the action is edited.
export interface EvalResult {
	readonly passed: boolean;
	readonly evaluatedAtUtc: number;
	readonly actionVersionAtEval: number;
	readonly modelName: string;
	// The number of golden cases actually evaluated this run.
	readonly goldenCaseCount: number;
	// The full enabled golden-set size before the per-run cap. When it exceeds goldenCaseCount the run evaluated only
	// a subset (the panel surfaces "evaluated N of TOTAL"). Defaulted to goldenCaseCount when an older backend
	// (predating the field) omits it.
	readonly goldenCaseTotal: number;
	readonly baselinePassCount: number;
	readonly candidatePassCount: number;
	readonly regressedCaseCount: number;
	readonly improvedCaseCount: number;
	readonly cases: readonly EvalCase[];
}

// The two states a P1 user can author (the create/edit form is constrained to these; the backend rejects the
// reserved Suggested/Archived states in this phase).
export const editablePlaybookActionStates: readonly Extract<PlaybookActionState, "Enabled" | "Disabled">[] = [
	"Enabled",
	"Disabled",
];

// Domain view-model for a playbook action. Timestamps are epoch milliseconds (long on the wire).
//
// Analysis provenance: an analysis-proposed action (source "Analysis", state "Suggested") carries
// the feedback ids that drove it (sourceFeedbackIds) and the analysis-agent confidence in [0,1] (confidence).
// Manual actions leave both null.
export interface PlaybookAction {
	readonly id: string;
	readonly agentDefinitionId: string;
	readonly state: PlaybookActionState;
	readonly source: PlaybookActionSource;
	readonly triggerCondition: string | null;
	readonly behavior: string;
	readonly scope: string | null;
	readonly priority: number;
	readonly version: number;
	readonly createdAtUtc: number;
	readonly updatedAtUtc: number;
	// Feedback message/conversation id GUIDs that drove an analysis-proposed action (null for manual actions).
	readonly sourceFeedbackIds: readonly string[] | null;
	// Analysis-agent confidence in [0,1] (null for manual actions).
	readonly confidence: number | null;
	// The latest regression-gate outcome. Null until an eval has run for this action (and cleared
	// when the action is edited). Gates the Approve/Promote control: promote is blocked until evalResult.passed.
	readonly evalResult: EvalResult | null;
}

// Form values authored in the panel: identity/version/timestamps/source/agentDefinitionId are managed by the
// backend and the panel, not edited as free text here. P1 authors only the injectable behavior, the enable
// state, the optional advisory fields, and the injection priority.
export interface PlaybookActionFormValues {
	behavior: string;
	state: Extract<PlaybookActionState, "Enabled" | "Disabled">;
	triggerCondition: string;
	scope: string;
	priority: number;
}

const editableStateSchema = z.enum(["Enabled", "Disabled"]);

// Zod schema validating the form before submit. Behavior is required (non-empty after trim); state is
// constrained to Enabled|Disabled (P1 scope); priority is any integer (ties broken server-side by CreatedAtUtc).
// triggerCondition/scope are free-text advisory fields, optional.
export const playbookActionFormSchema = z.object({
	behavior: z.string().trim().min(1).max(20000),
	state: editableStateSchema,
	triggerCondition: z.string().max(2000),
	scope: z.string().max(200),
	priority: z.number().int(),
});

export type PlaybookActionFormSchema = z.infer<typeof playbookActionFormSchema>;

// Empty form values for adding a new action. New actions default to Enabled at the end of the current order;
// the caller supplies the next priority so a fresh action sorts after the existing ones.
export function emptyPlaybookActionForm(nextPriority = 0): PlaybookActionFormValues {
	return {
		behavior: "",
		state: "Enabled",
		triggerCondition: "",
		scope: "",
		priority: nextPriority,
	};
}

// Project a persisted action back into editable form values (round-trip on edit). A reserved state
// (Suggested/Archived) that should never reach the P1 panel degrades to Disabled so the constrained editor can
// still render and re-save it safely.
export function toPlaybookActionFormValues(action: PlaybookAction): PlaybookActionFormValues {
	return {
		behavior: action.behavior,
		state: action.state === "Enabled" ? "Enabled" : "Disabled",
		triggerCondition: action.triggerCondition ?? "",
		scope: action.scope ?? "",
		priority: action.priority,
	};
}

// Request body for create/update. Source is omitted — the backend pins it to Manual in P1. Kept as the domain-side
// shape the form builders produce; the mapper widens it to the generated request type at the call boundary.
export interface SavePlaybookActionRequestDto {
	state: string;
	triggerCondition: string | null;
	behavior: string;
	scope: string | null;
	priority: number;
}

// Request body for editing a pending Suggested (analysis-provenance) action via the dedicated `/suggested` route.
// Carries NO `state` field — the backend pins state/source/evidence; the action stays Suggested until Approve. The
// manual PUT route 404s on an Analysis-provenance action, so a Suggested edit must use this body + route.
export interface SaveSuggestedActionRequestDto {
	behavior: string;
	triggerCondition: string | null;
	scope: string | null;
	priority: number;
}

// Stable ordering for display: ascending Priority, then CreatedAtUtc as a deterministic tiebreak (mirrors the
// store's ListEnabledByAgent ordering so the panel order matches the injection order).
export function comparePlaybookActions(a: PlaybookAction, b: PlaybookAction): number {
	if (a.priority !== b.priority) {
		return a.priority - b.priority;
	}
	return a.createdAtUtc - b.createdAtUtc;
}
