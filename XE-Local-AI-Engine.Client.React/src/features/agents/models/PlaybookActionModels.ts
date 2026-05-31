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

// Wire DTO (camelCase, matching the agents surface). The backend forces source=Manual in P1; the field is
// carried for display + forward-compat.
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
}

// Request body for create/update. Source is omitted — the backend pins it to Manual in P1.
export interface SavePlaybookActionRequestDto {
	state: string;
	triggerCondition: string | null;
	behavior: string;
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

// Stable ordering for display: ascending Priority, then CreatedAtUtc as a deterministic tiebreak (mirrors the
// store's ListEnabledByAgent ordering so the panel order matches the injection order).
export function comparePlaybookActions(a: PlaybookAction, b: PlaybookAction): number {
	if (a.priority !== b.priority) {
		return a.priority - b.priority;
	}
	return a.createdAtUtc - b.createdAtUtc;
}
