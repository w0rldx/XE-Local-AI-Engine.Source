import type { AxiosRequestConfig } from "axios";

import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import { parseToolCatalogSource, type ToolCatalogEntry } from "@/features/tools/models/ToolCatalogModels";

// Wire DTO (camelCase, matching the other Local API surfaces). Kept as a thin contract layer so the pickers
// work against the documented Lane 3 endpoint; if the backend casing/route base differs, only this file
// changes. Route base mirrors P3's GetToolCapableModelsEndpoint sibling — reconcile with Lane 3 if needed.
export interface ToolCatalogEntryDto {
	name: string;
	description: string | null;
	requiresApproval: boolean;
	source: string;
}

export interface ToolCatalogResponseDto {
	tools: ToolCatalogEntryDto[];
}

// GetToolCatalogEndpoint route. Single source so a route mismatch from Lane 3 is a one-line change.
const TOOL_CATALOG_ROUTE = "tool-catalog";

export function toToolCatalogEntry(dto: ToolCatalogEntryDto): ToolCatalogEntry {
	return {
		name: dto.name,
		description: dto.description ?? "",
		requiresApproval: dto.requiresApproval,
		// Backend `source` is "builtin" or the qualified "mcp:{serverSlug}" — parse to a typed kind + slug.
		source: parseToolCatalogSource(dto.source),
	};
}

export async function listToolCatalog(config?: AxiosRequestConfig): Promise<ToolCatalogEntry[]> {
	const { data } = await axiosInstance.get<ToolCatalogResponseDto>(buildLocalApiUrl(TOOL_CATALOG_ROUTE), config);
	return (data.tools ?? []).map(toToolCatalogEntry);
}
