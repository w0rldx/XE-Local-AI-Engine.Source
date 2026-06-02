import { ApiError } from "@/core/api/errors/ApiError";
import type {
	XeLocalAiEngineClientEndpointsAgentsV1CreatePlaybookActionRequest,
	XeLocalAiEngineClientEndpointsAgentsV1PlaybookActionResponse,
	XeLocalAiEngineClientEndpointsAgentsV1PlaybookEvalCaseResultResponse,
	XeLocalAiEngineClientEndpointsAgentsV1PlaybookEvalResultResponse,
	XeLocalAiEngineClientEndpointsAgentsV1UpdateSuggestedPlaybookActionRequest,
} from "@/core/api/generated";
import { type PromoteConflictStatus, parsePromoteConflictBody } from "@/features/agents/models/GoldenConversationModels";
import type {
	EvalCase,
	EvalResult,
	EvalScoredBy,
	PlaybookAction,
	PlaybookActionFormValues,
	PlaybookActionSource,
	PlaybookActionState,
	SavePlaybookActionRequestDto,
	SaveSuggestedActionRequestDto,
} from "@/features/agents/models/PlaybookActionModels";

// Maps the generated (OpenAPI) playbook-action response types to the stricter domain view-models the governance
// panel depends on. The generated types are the single source of truth for the wire shape; their fields are all
// optional (`x?: T`), so each mapper coalesces every field to a required value with a safe default. Boundary
// validation and ApiError convergence are owned by the generated zod validator (`validator: true`) + the
// withResponseValidation bridge at the hook — these mappers only project the already-validated wire shape into the
// immutable domain shape (they no longer re-validate, replacing the feature's former hand-zod safeParse). The
// generated enums (state / source) are string unions with the SAME values as the domain unions, so an enum maps
// through unchanged when present and falls back to a safe default when omitted. Redaction is the backend's: the
// mapper surfaces only what the response carries (eval results are plaintext-but-scoped; SourceFeedbackIds ride
// the response as-is) and never reconstructs a dropped field.

// The known wire values, mirrored from the domain unions, so a string-widened field can be narrowed with a guard.
const KNOWN_STATES: readonly PlaybookActionState[] = ["Suggested", "Enabled", "Disabled", "Archived"];
const KNOWN_SOURCES: readonly PlaybookActionSource[] = ["Manual", "Analysis"];

// Map a wire state to a known state, defaulting to Disabled (the safe non-injecting state) for an absent/unknown
// value rather than throwing — the panel keeps working if the backend grows the enum.
function toState(value: string | undefined): PlaybookActionState {
	return value !== undefined && KNOWN_STATES.includes(value as PlaybookActionState) ? (value as PlaybookActionState) : "Disabled";
}

// Map a wire source to a known source, defaulting to Manual (the P1 provenance) for an absent/unknown value so the
// panel always renders a provenance label.
function toSource(value: string | undefined): PlaybookActionSource {
	return value !== undefined && KNOWN_SOURCES.includes(value as PlaybookActionSource)
		? (value as PlaybookActionSource)
		: "Manual";
}

// Normalize a wire confidence to a [0,1] number or null. An absent/null/out-of-range value degrades to null so the
// row omits the confidence badge rather than rendering a nonsense percentage.
function toConfidence(value: number | null | undefined): number | null {
	return typeof value === "number" && value >= 0 && value <= 1 ? value : null;
}

// Map a wire eval-case `scoredBy` to a known scoring kind, defaulting to "assertion" for an absent/unknown value.
function toScoredBy(value: string | undefined): EvalScoredBy {
	return value === "judge" ? "judge" : "assertion";
}

function toEvalCase(dto: XeLocalAiEngineClientEndpointsAgentsV1PlaybookEvalCaseResultResponse): EvalCase {
	return {
		goldenCaseId: dto.goldenCaseId ?? "",
		scoredBy: toScoredBy(dto.scoredBy),
		baselinePass: dto.baselinePass ?? false,
		candidatePass: dto.candidatePass ?? false,
		regressed: dto.regressed ?? false,
	};
}

// Map the eval-gate outcome to a typed EvalResult or null. An absent/null nested object degrades to null so the
// Suggested row renders without the eval badge (and Approve stays disabled — no eval, no promote).
function toEvalResult(
	dto: XeLocalAiEngineClientEndpointsAgentsV1PlaybookEvalResultResponse | null | undefined,
): EvalResult | null {
	if (dto == null) {
		return null;
	}
	const goldenCaseCount = dto.goldenCaseCount ?? 0;
	return {
		passed: dto.passed ?? false,
		evaluatedAtUtc: dto.evaluatedAtUtc ?? 0,
		actionVersionAtEval: dto.actionVersionAtEval ?? 0,
		modelName: dto.modelName ?? "",
		goldenCaseCount,
		// Fall back to the evaluated count when the total is omitted (no truncation note then).
		goldenCaseTotal: dto.goldenCaseTotal ?? goldenCaseCount,
		baselinePassCount: dto.baselinePassCount ?? 0,
		candidatePassCount: dto.candidatePassCount ?? 0,
		regressedCaseCount: dto.regressedCaseCount ?? 0,
		improvedCaseCount: dto.improvedCaseCount ?? 0,
		cases: (dto.cases ?? []).map(toEvalCase),
	};
}

// Project a generated playbook-action response into the immutable domain view-model. Manual actions carry both
// analysis fields null; an analysis-proposed action (Playbook P3) carries source=Analysis with sourceFeedbackIds +
// confidence populated.
export function toPlaybookAction(dto: XeLocalAiEngineClientEndpointsAgentsV1PlaybookActionResponse): PlaybookAction {
	return {
		id: dto.id ?? "",
		agentDefinitionId: dto.agentDefinitionId ?? "",
		state: toState(dto.state),
		source: toSource(dto.source),
		triggerCondition: dto.triggerCondition ?? null,
		behavior: dto.behavior ?? "",
		scope: dto.scope ?? null,
		priority: dto.priority ?? 0,
		version: dto.version ?? 0,
		createdAtUtc: dto.createdAtUtc ?? 0,
		updatedAtUtc: dto.updatedAtUtc ?? 0,
		sourceFeedbackIds: dto.sourceFeedbackIds ?? null,
		confidence: toConfidence(dto.confidence),
		evalResult: toEvalResult(dto.evalResult),
	};
}

// Build a create/update save request body from form values. Behavior is trimmed; the optional advisory fields
// collapse blank strings to null so the stored row never carries an empty-string sentinel. The generated
// create/update request bodies are structurally identical, so one builder serves both.
export function toSavePlaybookActionRequest(
	form: PlaybookActionFormValues,
): SavePlaybookActionRequestDto & XeLocalAiEngineClientEndpointsAgentsV1CreatePlaybookActionRequest {
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

// Build a Suggested-edit request body from form values (the dedicated `/suggested` route). Same field handling as
// toSavePlaybookActionRequest but WITHOUT `state` — the backend keeps the action Suggested until it is approved.
export function toSaveSuggestedActionRequest(
	form: PlaybookActionFormValues,
): SaveSuggestedActionRequestDto & XeLocalAiEngineClientEndpointsAgentsV1UpdateSuggestedPlaybookActionRequest {
	const trimmedTrigger = form.triggerCondition.trim();
	const trimmedScope = form.scope.trim();

	return {
		behavior: form.behavior.trim(),
		triggerCondition: trimmedTrigger.length > 0 ? trimmedTrigger : null,
		scope: trimmedScope.length > 0 ? trimmedScope : null,
		priority: form.priority,
	};
}

// Narrow a domain save request (state is a `string` on the domain DTO) into the generated create/update request
// body (state is the generated PlaybookActionState union). The state always carries a valid form value
// (Enabled/Disabled), but is guarded so an unexpected value degrades to Disabled rather than violating the contract.
export function toPlaybookActionRequestBody(
	request: SavePlaybookActionRequestDto,
): XeLocalAiEngineClientEndpointsAgentsV1CreatePlaybookActionRequest {
	return {
		state: toState(request.state),
		triggerCondition: request.triggerCondition,
		behavior: request.behavior,
		scope: request.scope,
		priority: request.priority,
	};
}

// HTTP 409 — a blocked promote (the eval gate / enabled-set cap). The shared axios ProblemDetails interceptor wraps
// a non-2xx into an ApiError; we recover the typed { status, reason } body so the panel can explain WHY promotion is
// blocked.
const HTTP_CONFLICT = 409;

// Thrown by the promote mutation when the eval gate (or enabled cap) blocks a promote (HTTP 409). Carries the
// machine status + the human-readable reason from the conflict body so the panel renders the precise reason (needs
// eval / regressed / stale / cap reached) rather than a generic "could not update" message.
export class PromoteConflictError extends Error {
	constructor(
		readonly status: PromoteConflictStatus,
		reason: string,
	) {
		super(reason);
		this.name = "PromoteConflictError";
	}
}

// Translate a 409 eval-gate/cap rejection into a typed PromoteConflictError; any other error passes through
// unchanged. The shared interceptor wraps a non-2xx into an ApiError carrying the raw conflict body in
// apiProblemDetails. Returns the error to throw (the caller re-throws it).
export function toPromoteError(error: unknown): unknown {
	if (error instanceof ApiError && error.statusCode === HTTP_CONFLICT) {
		const conflict = parsePromoteConflictBody(error.apiProblemDetails);
		if (conflict) {
			return new PromoteConflictError(conflict.status, conflict.reason);
		}
	}
	return error;
}
