import { z } from "zod";

// Playbook P1 — manual playbook actions. Each action carries an injected behavior (the instruction text appended
// to the owning agent's system prompt when its playbook is enabled), an enable/disable state, a provenance
// source (P1 writes "Manual" only — the analysis-proposed "Analysis" source arrives in a later phase), an
// optional trigger condition + scope tag (advisory/display in P1), and a Priority that orders injection.
//
// Mirrors the backend PlaybookActionState (Suggested=0, Enabled=1, Disabled=2, Archived=3) and
// PlaybookActionSource (Manual=0, Analysis=1) enums; the wire contract carries the string form. P1 only ever
// shows/authors Enabled|Disabled state and Manual source — the other enum slots are reserved for later phases.

export type PlaybookActionState = "Suggested" | "Enabled" | "Disabled" | "Archived";

export type PlaybookActionSource = "Manual" | "Analysis";

// The two states a P1 user can author (the create/edit form is constrained to these; the backend rejects the
// reserved Suggested/Archived states in this phase).
export const editablePlaybookActionStates: readonly Extract<PlaybookActionState, "Enabled" | "Disabled">[] = [
	"Enabled",
	"Disabled",
];

// Domain view-model for a playbook action. Timestamps are epoch milliseconds (long on the wire).
//
// Playbook P3 adds analysis provenance: an analysis-proposed action (source "Analysis", state "Suggested") carries
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

const stateSchema = z.enum(["Suggested", "Enabled", "Disabled", "Archived"]);
const sourceSchema = z.enum(["Manual", "Analysis"]);
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

// Wire DTO (camelCase, matching the agents surface). Manual actions carry source=Manual with both analysis
// fields null; an analysis-proposed action (Playbook P3) carries source=Analysis with sourceFeedbackIds +
// confidence populated. Both new fields are typed optional/nullable so an older backend (omitting them) parses.
export interface PlaybookActionDto {
	id: string;
	agentDefinitionId: string;
	state: string;
	source: string;
	triggerCondition: string | null;
	behavior: string;
	scope: string | null;
	priority: number;
	version: number;
	createdAtUtc: number;
	updatedAtUtc: number;
	sourceFeedbackIds?: string[] | null;
	confidence?: number | null;
}

// Request body for create/update. Source is omitted — the backend pins it to Manual in P1.
export interface SavePlaybookActionRequestDto {
	state: string;
	triggerCondition: string | null;
	behavior: string;
	scope: string | null;
	priority: number;
}

// Request body for editing a pending Suggested (analysis-provenance) action via the dedicated `/suggested` route.
// Carries NO `state` field — the backend pins state/source/evidence; the action stays Suggested until Approve. The
// manual PUT route now 404s on an Analysis-provenance action, so a Suggested edit must use this body + route.
export interface SaveSuggestedActionRequestDto {
	behavior: string;
	triggerCondition: string | null;
	scope: string | null;
	priority: number;
}

// Map an unknown wire string to a known state, defaulting to Disabled (the safe non-injecting state) for an
// unrecognized value rather than throwing — the panel keeps working if the backend grows the enum.
function normalizeState(value: string): PlaybookActionState {
	return stateSchema.safeParse(value).success ? (value as PlaybookActionState) : "Disabled";
}

// Map an unknown wire string to a known source, defaulting to Manual (the P1 provenance) for an unrecognized
// value so the panel always renders a provenance label.
function normalizeSource(value: string): PlaybookActionSource {
	return sourceSchema.safeParse(value).success ? (value as PlaybookActionSource) : "Manual";
}

// Boundary schemas for the Playbook P3 analysis fields. Both are nullable/optional: a manual action (or an older
// backend that predates P3) omits them. Defensive — an unexpected/garbage value degrades to null rather than
// throwing the whole list parse, so one malformed action can't blank the panel.
const sourceFeedbackIdsSchema = z.array(z.string()).nullish();
const confidenceSchema = z.number().min(0).max(1).nullish();

// Normalize the cited feedback ids to a string[] or null. An absent/null/invalid value (wrong type, non-string
// members) degrades to null so the row simply renders without an evidence count.
function normalizeSourceFeedbackIds(value: unknown): readonly string[] | null {
	const result = sourceFeedbackIdsSchema.safeParse(value);
	return result.success && result.data != null ? result.data : null;
}

// Normalize the analysis confidence to a [0,1] number or null. An absent/null/out-of-range/non-number value
// degrades to null so the row omits the confidence badge rather than rendering a nonsense percentage.
function normalizeConfidence(value: unknown): number | null {
	const result = confidenceSchema.safeParse(value);
	return result.success && result.data != null ? result.data : null;
}

export function toPlaybookAction(dto: PlaybookActionDto): PlaybookAction {
	return {
		id: dto.id,
		agentDefinitionId: dto.agentDefinitionId,
		state: normalizeState(dto.state),
		source: normalizeSource(dto.source),
		triggerCondition: dto.triggerCondition ?? null,
		behavior: dto.behavior,
		scope: dto.scope ?? null,
		priority: dto.priority,
		version: dto.version,
		createdAtUtc: dto.createdAtUtc,
		updatedAtUtc: dto.updatedAtUtc,
		sourceFeedbackIds: normalizeSourceFeedbackIds(dto.sourceFeedbackIds),
		confidence: normalizeConfidence(dto.confidence),
	};
}

// Build a save request from form values. Behavior is trimmed; the optional advisory fields collapse blank
// strings to null so the stored row never carries an empty-string sentinel.
export function toSavePlaybookActionRequest(form: PlaybookActionFormValues): SavePlaybookActionRequestDto {
	const trimmedTrigger = form.triggerCondition.trim();
	const trimmedScope = form.scope.trim();

	return {
		state: form.state,
		triggerCondition: trimmedTrigger.length > 0 ? trimmedTrigger : null,
		behavior: form.behavior.trim(),
		scope: trimmedScope.length > 0 ? trimmedScope : null,
		priority: form.priority,
	};
}

// Build a Suggested-edit request from form values (the dedicated `/suggested` route). Same field handling as
// toSavePlaybookActionRequest but WITHOUT `state` — the backend keeps the action Suggested until it is approved.
export function toSaveSuggestedActionRequest(form: PlaybookActionFormValues): SaveSuggestedActionRequestDto {
	const trimmedTrigger = form.triggerCondition.trim();
	const trimmedScope = form.scope.trim();

	return {
		behavior: form.behavior.trim(),
		triggerCondition: trimmedTrigger.length > 0 ? trimmedTrigger : null,
		scope: trimmedScope.length > 0 ? trimmedScope : null,
		priority: form.priority,
	};
}

// Stable ordering for display: ascending Priority, then CreatedAtUtc as a deterministic tiebreak (mirrors the
// store's ListEnabledByAgent ordering so the panel order matches the injection order).
export function comparePlaybookActions(a: PlaybookAction, b: PlaybookAction): number {
	if (a.priority !== b.priority) {
		return a.priority - b.priority;
	}
	return a.createdAtUtc - b.createdAtUtc;
}
