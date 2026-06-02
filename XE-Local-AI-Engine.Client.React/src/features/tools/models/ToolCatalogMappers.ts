import type { XeLocalAiEngineClientEndpointsMcpV1ToolCatalogEntryResponse } from "@/core/api/generated";
import { parseToolCatalogSource, type ToolCatalogEntry } from "@/features/tools/models/ToolCatalogModels";

// Maps the generated (OpenAPI) tool-catalog entry response to the stricter domain view-model the tool pickers
// depend on. The generated type is the single source of truth for the wire shape; its fields are all optional
// (`x?: T`), so each field coalesces to a required value. The backend `source` is "builtin" or the qualified
// "mcp:{serverSlug}" — parse to a typed kind + slug so the UI can group/badge tools by their originating server.
export function toToolCatalogEntry(dto: XeLocalAiEngineClientEndpointsMcpV1ToolCatalogEntryResponse): ToolCatalogEntry {
	return {
		name: dto.name ?? "",
		description: dto.description ?? "",
		requiresApproval: dto.requiresApproval ?? false,
		source: parseToolCatalogSource(dto.source ?? "builtin"),
	};
}
