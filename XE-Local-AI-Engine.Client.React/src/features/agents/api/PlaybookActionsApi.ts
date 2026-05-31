import type { AxiosRequestConfig } from "axios";

import { ApiError } from "@/core/api/errors/ApiError";
import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import {
	parsePromoteConflictBody,
	type PromoteConflictStatus,
} from "@/features/agents/models/GoldenConversationModels";
import {
	type PlaybookAction,
	type PlaybookActionDto,
	type SavePlaybookActionRequestDto,
	type SaveSuggestedActionRequestDto,
	toPlaybookAction,
} from "@/features/agents/models/PlaybookActionModels";

// Playbook P1/P3 — CRUD + governance against the per-agent playbook routes (Operator-gated, mirror
// AgentDefinitionsApi):
//   GET    /agents/{agentDefinitionId}/playbook                    → list actions
//   POST   /agents/{agentDefinitionId}/playbook                    → create action
//   PUT    /agents/{agentDefinitionId}/playbook/{actionId}         → update (enable/disable + reorder via priority)
//   DELETE /agents/{agentDefinitionId}/playbook/{actionId}         → delete
//   POST   /agents/{agentDefinitionId}/playbook/analyze             → run analysis, returns newly-created Suggested actions (P3)
//   POST   /agents/{agentDefinitionId}/playbook/{actionId}/promote  → Suggested → Enabled (P3)
//   POST   /agents/{agentDefinitionId}/playbook/{actionId}/reject   → Suggested → Archived (P3)
//   PUT    /agents/{agentDefinitionId}/playbook/{actionId}/suggested→ edit a pending Suggested action (stays Suggested) (P3)
// The manual PUT route 404s on an Analysis-provenance action, so a Suggested edit MUST use the `/suggested` route.
// Kept as a thin contract layer so the panel works against the documented Lane 3 endpoint; if the backend
// casing/route base differs, only this file changes. All reads wire the TanStack Query AbortSignal in.

export interface ListPlaybookActionsResponseDto {
	items: PlaybookActionDto[];
}

const AGENTS_ROUTE = "agents";
const PLAYBOOK_SEGMENT = "playbook";
const ANALYZE_SEGMENT = "analyze";
const PROMOTE_SEGMENT = "promote";
const REJECT_SEGMENT = "reject";
const SUGGESTED_SEGMENT = "suggested";
const EVAL_SEGMENT = "eval";

// HTTP 409 — a blocked promote (the eval gate). The route base's problem-details interceptor wraps a non-2xx into
// an ApiError; we recover the typed {status, reason} body so the panel can explain WHY promotion is blocked.
const HTTP_CONFLICT = 409;

// Thrown by promoteSuggested when the eval gate blocks a promote (HTTP 409). Carries the machine status + the
// human-readable reason from the conflict body so the panel renders the precise reason (needs eval / regressed /
// stale) rather than a generic "could not update" message.
export class PromoteConflictError extends Error {
	constructor(
		readonly status: PromoteConflictStatus,
		reason: string,
	) {
		super(reason);
		this.name = "PromoteConflictError";
	}
}

function playbookRoute(agentDefinitionId: string): string {
	return `${AGENTS_ROUTE}/${encodeURIComponent(agentDefinitionId)}/${PLAYBOOK_SEGMENT}`;
}

function playbookActionRoute(agentDefinitionId: string, actionId: string): string {
	return `${playbookRoute(agentDefinitionId)}/${encodeURIComponent(actionId)}`;
}

export async function listPlaybookActions(
	agentDefinitionId: string,
	config?: AxiosRequestConfig,
): Promise<PlaybookAction[]> {
	const { data } = await axiosInstance.get<ListPlaybookActionsResponseDto>(
		buildLocalApiUrl(playbookRoute(agentDefinitionId)),
		config,
	);
	return (data.items ?? []).map(toPlaybookAction);
}

export async function createPlaybookAction(
	agentDefinitionId: string,
	request: SavePlaybookActionRequestDto,
	config?: AxiosRequestConfig,
): Promise<PlaybookAction> {
	const { data } = await axiosInstance.post<PlaybookActionDto>(
		buildLocalApiUrl(playbookRoute(agentDefinitionId)),
		request,
		config,
	);
	return toPlaybookAction(data);
}

export async function updatePlaybookAction(
	agentDefinitionId: string,
	actionId: string,
	request: SavePlaybookActionRequestDto,
	config?: AxiosRequestConfig,
): Promise<PlaybookAction> {
	const { data } = await axiosInstance.put<PlaybookActionDto>(
		buildLocalApiUrl(playbookActionRoute(agentDefinitionId, actionId)),
		request,
		config,
	);
	return toPlaybookAction(data);
}

// Edit a pending Suggested (analysis-provenance) action via the dedicated route. The action stays Suggested; the
// backend pins state/source/evidence and ignores any state in the body (there is none). Manual edits keep using
// updatePlaybookAction — the manual PUT 404s on an Analysis-provenance action.
export async function updateSuggested(
	agentDefinitionId: string,
	actionId: string,
	request: SaveSuggestedActionRequestDto,
	config?: AxiosRequestConfig,
): Promise<PlaybookAction> {
	const { data } = await axiosInstance.put<PlaybookActionDto>(
		buildLocalApiUrl(`${playbookActionRoute(agentDefinitionId, actionId)}/${SUGGESTED_SEGMENT}`),
		request,
		config,
	);
	return toPlaybookAction(data);
}

export async function deletePlaybookAction(
	agentDefinitionId: string,
	actionId: string,
	config?: AxiosRequestConfig,
): Promise<void> {
	await axiosInstance.delete(buildLocalApiUrl(playbookActionRoute(agentDefinitionId, actionId)), config);
}

// Run the analysis agent for this agent: it proposes Suggested actions (source=Analysis) from the collected
// feedback. Returns the newly-created Suggested actions in the same { items } envelope as the list endpoint
// (empty array when the agent surfaces no new suggestions).
export async function analyzePlaybook(
	agentDefinitionId: string,
	config?: AxiosRequestConfig,
): Promise<PlaybookAction[]> {
	// Send an empty JSON object body (not undefined) so axios sets Content-Type: application/json — FastEndpoints
	// returns 415 for a bodyless POST. The route params carry the ids; the body is intentionally empty.
	const { data } = await axiosInstance.post<ListPlaybookActionsResponseDto>(
		buildLocalApiUrl(`${playbookRoute(agentDefinitionId)}/${ANALYZE_SEGMENT}`),
		{},
		config,
	);
	return (data.items ?? []).map(toPlaybookAction);
}

// Promote a Suggested analysis action to Enabled (operator approves the proposal). Returns the updated action.
// Playbook P4 — the promote is eval-gated: when the eval has not passed the backend returns 409 with a typed
// { status, reason } body, which we surface as a PromoteConflictError so the panel can show the precise reason.
export async function promoteSuggested(
	agentDefinitionId: string,
	actionId: string,
	config?: AxiosRequestConfig,
): Promise<PlaybookAction> {
	try {
		// Empty JSON object body (not undefined) — FastEndpoints 415s a bodyless POST; ids ride the route.
		const { data } = await axiosInstance.post<PlaybookActionDto>(
			buildLocalApiUrl(`${playbookActionRoute(agentDefinitionId, actionId)}/${PROMOTE_SEGMENT}`),
			{},
			config,
		);
		return toPlaybookAction(data);
	} catch (error) {
		throw toPromoteError(error);
	}
}

// Translate a 409 eval-gate rejection into a typed PromoteConflictError; any other error passes through unchanged.
// The route base's interceptor wraps a non-2xx into an ApiError carrying the raw conflict body in apiProblemDetails.
function toPromoteError(error: unknown): unknown {
	if (error instanceof ApiError && error.statusCode === HTTP_CONFLICT) {
		const conflict = parsePromoteConflictBody(error.apiProblemDetails);
		if (conflict) {
			return new PromoteConflictError(conflict.status, conflict.reason);
		}
	}
	return error;
}

// Reject a Suggested analysis action (Suggested → Archived). Returns the updated action.
export async function rejectSuggested(
	agentDefinitionId: string,
	actionId: string,
	config?: AxiosRequestConfig,
): Promise<PlaybookAction> {
	// Empty JSON object body (not undefined) — FastEndpoints 415s a bodyless POST; ids ride the route.
	const { data } = await axiosInstance.post<PlaybookActionDto>(
		buildLocalApiUrl(`${playbookActionRoute(agentDefinitionId, actionId)}/${REJECT_SEGMENT}`),
		{},
		config,
	);
	return toPlaybookAction(data);
}

// Playbook P4 — run the eval gate for a Suggested action against the agent's golden conversation set. Records and
// returns the updated action (now carrying evalResult). 404 when the action is unknown/cross-agent/not-pending.
export async function runEval(
	agentDefinitionId: string,
	actionId: string,
	config?: AxiosRequestConfig,
): Promise<PlaybookAction> {
	// Empty JSON object body (not undefined) — FastEndpoints 415s a bodyless POST; ids ride the route.
	const { data } = await axiosInstance.post<PlaybookActionDto>(
		buildLocalApiUrl(`${playbookActionRoute(agentDefinitionId, actionId)}/${EVAL_SEGMENT}`),
		{},
		config,
	);
	return toPlaybookAction(data);
}
