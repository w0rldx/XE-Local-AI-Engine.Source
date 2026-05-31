import type { AxiosRequestConfig } from "axios";

import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import type {
	AgentDefinition,
	AgentDefinitionFormValues,
	AgentDefinitionKind,
} from "@/features/agents/models/AgentDefinitionModels";
import { serializeOrchestrationTopology } from "@/features/agents/models/OrchestrationTopologyModels";
import type { ReasoningEffort } from "@/features/chat/models/ChatModels";

// Wire DTOs (camelCase, matching LocalModelsApi.ts). Kept as a thin contract layer so the page works against
// the documented Lane 3 endpoint; if the backend casing/route base differs, only this file changes.
export interface AgentDefinitionDto {
	id: string;
	name: string;
	description: string | null;
	instructions: string;
	modelProfile: string | null;
	reasoningEffort: string | null;
	kind: AgentDefinitionKind;
	allowedToolNames: string[];
	toolApprovals: Record<string, boolean>;
	orchestrationTopologyJson: string | null;
	// Playbook P1: rides the existing agent DTOs (like orchestrationTopologyJson) — no new agent endpoint.
	playbookEnabled: boolean;
	version: number;
	createdAtUtc: number;
	updatedAtUtc: number;
}

export interface ListAgentDefinitionsResponseDto {
	items: AgentDefinitionDto[];
}

export interface SaveAgentDefinitionRequestDto {
	name: string;
	description: string | null;
	instructions: string;
	modelProfile: string | null;
	reasoningEffort: string | null;
	kind: AgentDefinitionKind;
	allowedToolNames: string[];
	toolApprovals: Record<string, boolean>;
	// Raw orchestration topology JSON (orchestration). null for Single definitions; for Orchestrator definitions it is the
	// serialized handoff topology (see OrchestrationTopologyModels). The backend persists and validates it.
	orchestrationTopologyJson: string | null;
	// Playbook P1: toggles whether the agent's enabled playbook actions are injected at resolve time.
	playbookEnabled: boolean;
}

const AGENTS_ROUTE = "agents";

function normalizeReasoningEffort(value: string | null): ReasoningEffort | null {
	if (value === "none" || value === "low" || value === "medium" || value === "high") {
		return value;
	}

	return null;
}

export function toAgentDefinition(dto: AgentDefinitionDto): AgentDefinition {
	return {
		id: dto.id,
		name: dto.name,
		description: dto.description ?? "",
		instructions: dto.instructions,
		modelProfile: dto.modelProfile,
		reasoningEffort: normalizeReasoningEffort(dto.reasoningEffort),
		kind: dto.kind,
		allowedToolNames: dto.allowedToolNames ?? [],
		toolApprovals: dto.toolApprovals ?? {},
		orchestrationTopologyJson: dto.orchestrationTopologyJson,
		playbookEnabled: dto.playbookEnabled ?? false,
		version: dto.version,
		createdAtUtc: dto.createdAtUtc,
		updatedAtUtc: dto.updatedAtUtc,
	};
}

// Build the save request from the form. `triageAgentDefinitionId` is the orchestrator definition's own id — known
// on edit, empty on create (the backend assigns identity and the triage is re-pinned on the next edit). The
// orchestration topology is serialized ONLY for Orchestrator definitions; a Single definition sends null so a
// stale topology never leaks into a non-orchestrator.
export function toSaveAgentDefinitionRequest(
	form: AgentDefinitionFormValues,
	triageAgentDefinitionId = "",
): SaveAgentDefinitionRequestDto {
	const trimmedDescription = form.description.trim();

	return {
		name: form.name.trim(),
		description: trimmedDescription.length > 0 ? trimmedDescription : null,
		instructions: form.instructions.trim(),
		modelProfile: form.modelProfile,
		reasoningEffort: form.reasoningEffort,
		kind: form.kind,
		allowedToolNames: form.allowedToolNames,
		// Persist only approvals for currently selected tools so the stored map never drifts from the tool list.
		toolApprovals: Object.fromEntries(
			Object.entries(form.toolApprovals).filter(([toolName]) => form.allowedToolNames.includes(toolName)),
		),
		orchestrationTopologyJson:
			form.kind === "Orchestrator"
				? serializeOrchestrationTopology(form.orchestration, triageAgentDefinitionId)
				: null,
		playbookEnabled: form.playbookEnabled,
	};
}

export async function listAgentDefinitions(config?: AxiosRequestConfig): Promise<AgentDefinition[]> {
	const { data } = await axiosInstance.get<ListAgentDefinitionsResponseDto>(buildLocalApiUrl(AGENTS_ROUTE), config);
	return (data.items ?? []).map(toAgentDefinition);
}

export async function createAgentDefinition(
	request: SaveAgentDefinitionRequestDto,
	config?: AxiosRequestConfig,
): Promise<AgentDefinition> {
	const { data } = await axiosInstance.post<AgentDefinitionDto>(buildLocalApiUrl(AGENTS_ROUTE), request, config);
	return toAgentDefinition(data);
}

export async function updateAgentDefinition(
	id: string,
	request: SaveAgentDefinitionRequestDto,
	config?: AxiosRequestConfig,
): Promise<AgentDefinition> {
	const { data } = await axiosInstance.put<AgentDefinitionDto>(
		buildLocalApiUrl(`${AGENTS_ROUTE}/${encodeURIComponent(id)}`),
		request,
		config,
	);
	return toAgentDefinition(data);
}

export async function deleteAgentDefinition(id: string, config?: AxiosRequestConfig): Promise<void> {
	await axiosInstance.delete(buildLocalApiUrl(`${AGENTS_ROUTE}/${encodeURIComponent(id)}`), config);
}

// Tool-capable model names (backend AgentHomeOptions.ToolCapableModels). Self-contained read so the agents
// page can disable tool selection when a non-tool-capable model is pinned. Returns [] until Lane 3 ships it.
export interface ToolCapableModelsResponseDto {
	models: string[];
}

export async function listToolCapableModels(config?: AxiosRequestConfig): Promise<string[]> {
	const { data } = await axiosInstance.get<ToolCapableModelsResponseDto>(
		buildLocalApiUrl(`${AGENTS_ROUTE}/tool-capable-models`),
		config,
	);
	return data.models ?? [];
}
