import type { AxiosRequestConfig } from "axios";

import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import type {
	McpEnvEntry,
	McpServerFormValues,
	McpServerRegistration,
	McpTransportKind,
} from "@/features/mcp/models/McpServerModels";
import type {
	McpConnectionStatus,
	McpServerToolsView,
} from "@/features/mcp/models/McpServerToolsModels";

// Wire DTOs (camelCase, matching the other Local API surfaces). Kept as a thin contract layer so the page
// works against the documented Lane 3 endpoint; if the backend casing/route base differs, only this file
// changes. env is a map<string,string> on the wire (the form edits it as an ordered key/value list).
export interface McpServerDto {
	id: string;
	name: string;
	description: string | null;
	transportKind: McpTransportKind;
	command: string | null;
	arguments: string[] | null;
	workingDirectory: string | null;
	env: Record<string, string> | null;
	url: string | null;
	enabled: boolean;
	version: number;
	createdAtUtc: number;
	updatedAtUtc: number;
}

export interface ListMcpServersResponseDto {
	items: McpServerDto[];
}

export interface SaveMcpServerRequestDto {
	name: string;
	description: string | null;
	transportKind: McpTransportKind;
	command: string | null;
	arguments: string[];
	workingDirectory: string | null;
	env: Record<string, string>;
	url: string | null;
}

export interface McpDiscoveredToolDto {
	name: string;
	description: string | null;
	requiresApproval: boolean;
}

export interface McpServerToolsResponseDto {
	status: McpConnectionStatus;
	error: string | null;
	tools: McpDiscoveredToolDto[];
}

// MCP CRUD route base. Single source so a route mismatch from Lane 3 is a one-line change. Enable/disable are
// modeled as PATCH sub-routes (plan §6.1 "Enable/Disable (or PATCH)"); reconcile verb/path with Lane 3.
const MCP_ROUTE = "mcp/servers";

function envMapToEntries(env: Record<string, string> | null): McpEnvEntry[] {
	if (!env) {
		return [];
	}
	return Object.entries(env).map(([key, value]) => ({ key, value }));
}

function envEntriesToMap(entries: readonly McpEnvEntry[]): Record<string, string> {
	// Only persist rows with a non-empty key; last write wins on a duplicate key (the form allows transient
	// duplicates while typing — the stored map can never carry a blank key).
	const result: Record<string, string> = {};
	for (const entry of entries) {
		const key = entry.key.trim();
		if (key.length > 0) {
			result[key] = entry.value;
		}
	}
	return result;
}

export function toMcpServerRegistration(dto: McpServerDto): McpServerRegistration {
	return {
		id: dto.id,
		name: dto.name,
		description: dto.description ?? "",
		transportKind: dto.transportKind,
		command: dto.command,
		arguments: dto.arguments ?? [],
		workingDirectory: dto.workingDirectory,
		env: envMapToEntries(dto.env),
		url: dto.url,
		enabled: dto.enabled,
		version: dto.version,
		createdAtUtc: dto.createdAtUtc,
		updatedAtUtc: dto.updatedAtUtc,
	};
}

export function toSaveMcpServerRequest(form: McpServerFormValues): SaveMcpServerRequestDto {
	const trimmedDescription = form.description.trim();
	const isStdio = form.transportKind === "Stdio";

	return {
		name: form.name.trim(),
		description: trimmedDescription.length > 0 ? trimmedDescription : null,
		transportKind: form.transportKind,
		// Persist only the transport-relevant fields so the stored row never carries cross-transport leftovers.
		command: isStdio && form.command.trim().length > 0 ? form.command.trim() : null,
		arguments: isStdio ? form.arguments.filter((argument) => argument.length > 0) : [],
		workingDirectory:
			isStdio && form.workingDirectory.trim().length > 0 ? form.workingDirectory.trim() : null,
		env: isStdio ? envEntriesToMap(form.env) : {},
		url: !isStdio && form.url.trim().length > 0 ? form.url.trim() : null,
	};
}

export function toMcpServerToolsView(dto: McpServerToolsResponseDto): McpServerToolsView {
	return {
		status: dto.status,
		error: dto.error,
		tools: (dto.tools ?? []).map((tool) => ({
			name: tool.name,
			description: tool.description ?? "",
			requiresApproval: tool.requiresApproval,
		})),
	};
}

export async function listMcpServers(config?: AxiosRequestConfig): Promise<McpServerRegistration[]> {
	const { data } = await axiosInstance.get<ListMcpServersResponseDto>(buildLocalApiUrl(MCP_ROUTE), config);
	return (data.items ?? []).map(toMcpServerRegistration);
}

export async function createMcpServer(
	request: SaveMcpServerRequestDto,
	config?: AxiosRequestConfig,
): Promise<McpServerRegistration> {
	const { data } = await axiosInstance.post<McpServerDto>(buildLocalApiUrl(MCP_ROUTE), request, config);
	return toMcpServerRegistration(data);
}

export async function updateMcpServer(
	id: string,
	request: SaveMcpServerRequestDto,
	config?: AxiosRequestConfig,
): Promise<McpServerRegistration> {
	const { data } = await axiosInstance.put<McpServerDto>(
		buildLocalApiUrl(`${MCP_ROUTE}/${encodeURIComponent(id)}`),
		request,
		config,
	);
	return toMcpServerRegistration(data);
}

export async function deleteMcpServer(id: string, config?: AxiosRequestConfig): Promise<void> {
	await axiosInstance.delete(buildLocalApiUrl(`${MCP_ROUTE}/${encodeURIComponent(id)}`), config);
}

export async function setMcpServerEnabled(
	id: string,
	enabled: boolean,
	config?: AxiosRequestConfig,
): Promise<McpServerRegistration> {
	const { data } = await axiosInstance.patch<McpServerDto>(
		buildLocalApiUrl(`${MCP_ROUTE}/${encodeURIComponent(id)}/enabled`),
		{ enabled },
		config,
	);
	return toMcpServerRegistration(data);
}

export async function getMcpServerTools(
	id: string,
	config?: AxiosRequestConfig,
): Promise<McpServerToolsView> {
	const { data } = await axiosInstance.get<McpServerToolsResponseDto>(
		buildLocalApiUrl(`${MCP_ROUTE}/${encodeURIComponent(id)}/tools`),
		config,
	);
	return toMcpServerToolsView(data);
}
