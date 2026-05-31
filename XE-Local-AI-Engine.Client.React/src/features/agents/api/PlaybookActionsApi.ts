import type { AxiosRequestConfig } from "axios";

import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
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
export async function promoteSuggested(
	agentDefinitionId: string,
	actionId: string,
	config?: AxiosRequestConfig,
): Promise<PlaybookAction> {
	// Empty JSON object body (not undefined) — FastEndpoints 415s a bodyless POST; ids ride the route.
	const { data } = await axiosInstance.post<PlaybookActionDto>(
		buildLocalApiUrl(`${playbookActionRoute(agentDefinitionId, actionId)}/${PROMOTE_SEGMENT}`),
		{},
		config,
	);
	return toPlaybookAction(data);
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
