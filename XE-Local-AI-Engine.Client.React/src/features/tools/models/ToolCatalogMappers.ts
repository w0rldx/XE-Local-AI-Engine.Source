import type { XeLocalAiEngineClientEndpointsMcpV1ToolCatalogEntryResponse } from "@/core/api/generated";
import { parseToolCatalogSource, parseToolCategory, type ToolCatalogEntry } from "@/features/tools/models/ToolCatalogModels";

// Maps the generated (OpenAPI) tool-catalog entry response to the stricter domain view-model the tool pickers
// depend on. The generated type is the single source of truth for the wire shape; its fields are all optional
// (`x?: T`), so each field coalesces to a required value. The backend `source` is "builtin" or the qualified
// "mcp:{serverSlug}" — parse to a typed kind + slug so the UI can group/badge tools by their originating server.
// category parses to the typed risk class (fail-closed to "Unknown"); effectiveRequiresApproval is the node-policy
// floor (fail-closed to true when the field is absent, so a badge never under-reports the gating).
// sessionScopeEligible defaults to TRUE when absent, which keeps the pre-field behaviour (the approval card offered
// session scope for every tool). Fail-closed makes no sense here: the value only decides whether a UI button is shown,
// and the node re-decides authoritatively on the decision itself, so an absent field must not silently remove a
// control that works.
export function toToolCatalogEntry(dto: XeLocalAiEngineClientEndpointsMcpV1ToolCatalogEntryResponse): ToolCatalogEntry {
	return {
		name: dto.name ?? "",
		description: dto.description ?? "",
		requiresApproval: dto.requiresApproval ?? false,
		source: parseToolCatalogSource(dto.source ?? "builtin"),
		category: parseToolCategory(dto.category ?? "Unknown"),
		effectiveRequiresApproval: dto.effectiveRequiresApproval ?? true,
		sessionScopeEligible: dto.sessionScopeEligible ?? true,
	};
}
