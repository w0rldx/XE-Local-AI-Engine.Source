import type { AxiosRequestConfig } from "axios";

import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import {
	type PlaybookAction,
	type PlaybookActionDto,
	type SavePlaybookActionRequestDto,
	toPlaybookAction,
} from "@/features/agents/models/PlaybookActionModels";

// Playbook P1 — CRUD against the per-agent playbook routes (Operator-gated, mirror AgentDefinitionsApi):
//   GET    /agents/{agentDefinitionId}/playbook            → list actions
//   POST   /agents/{agentDefinitionId}/playbook            → create action
//   PUT    /agents/{agentDefinitionId}/playbook/{actionId} → update (enable/disable + reorder via priority)
//   DELETE /agents/{agentDefinitionId}/playbook/{actionId} → delete
// Kept as a thin contract layer so the panel works against the documented Lane 3 endpoint; if the backend
// casing/route base differs, only this file changes. All reads wire the TanStack Query AbortSignal in.

export interface ListPlaybookActionsResponseDto {
	items: PlaybookActionDto[];
}

const AGENTS_ROUTE = "agents";
const PLAYBOOK_SEGMENT = "playbook";

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

export async function deletePlaybookAction(
	agentDefinitionId: string,
	actionId: string,
	config?: AxiosRequestConfig,
): Promise<void> {
	await axiosInstance.delete(buildLocalApiUrl(playbookActionRoute(agentDefinitionId, actionId)), config);
}
