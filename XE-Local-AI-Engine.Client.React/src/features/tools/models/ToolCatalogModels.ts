// Dynamic tool-catalog entry returned by the node GetToolCatalog endpoint (dynamic tool-catalog). The catalog is the
// single source the tool pickers consume — built-in node tools plus the tools discovered from enabled MCP
// servers. It replaces the static localToolCatalog const that the chat/agent surfaces previously rendered.

// Where a catalog entry originates. The backend `source` string is "builtin" for node built-ins (time/calc),
// the qualified form "mcp:{serverSlug}" for a tool discovered from a specific MCP server (the slug lets the UI
// group/badge tools by their originating server), or "custom" for a user-defined custom tool (host command /
// outbound fetch — rendered with a danger badge). ToolCatalogSourceKind is the coarse classification.
export type ToolCatalogSourceKind = "builtin" | "mcp" | "custom";

// Parsed source: a coarse kind plus, for MCP tools, the originating server slug (null for built-ins).
export interface ToolCatalogSource {
	readonly kind: ToolCatalogSourceKind;
	readonly serverSlug: string | null;
}

const MCP_SOURCE_PREFIX = "mcp:";

// Parse the backend source string into a typed source. "builtin" → builtin; "mcp:{slug}" → mcp + slug. An
// unrecognized or bare "mcp" value falls back to mcp with a null slug (never throws — display must degrade
// gracefully if the backend ever emits an unexpected form).
export function parseToolCatalogSource(raw: string): ToolCatalogSource {
	if (raw === "builtin") {
		return { kind: "builtin", serverSlug: null };
	}
	if (raw === "custom") {
		return { kind: "custom", serverSlug: null };
	}
	if (raw.startsWith(MCP_SOURCE_PREFIX)) {
		const slug = raw.slice(MCP_SOURCE_PREFIX.length);
		return { kind: "mcp", serverSlug: slug.length > 0 ? slug : null };
	}
	if (raw === "mcp") {
		return { kind: "mcp", serverSlug: null };
	}
	// Unknown source string: treat as built-in so the tool still renders without a server badge.
	return { kind: "builtin", serverSlug: null };
}

// A tool's risk class, mirroring the backend ToolCategory enum names. The node-default approval policy keys
// off this to decide whether a whole class of tools must be approval-gated. "Unknown" is the fail-closed default: a
// tool with no declared class is treated as approval-requiring.
export type ToolCategory = "ReadLocal" | "WriteExecute" | "Orchestration" | "Network" | "Unknown";

const TOOL_CATEGORIES: ReadonlySet<ToolCategory> = new Set<ToolCategory>([
	"ReadLocal",
	"WriteExecute",
	"Orchestration",
	"Network",
	"Unknown",
]);

// Parse the backend category name into the typed union. An unrecognized/blank value falls back to "Unknown" so the
// badge fails closed (treated as approval-requiring) rather than throwing or showing an untranslated raw string.
export function parseToolCategory(raw: string): ToolCategory {
	return TOOL_CATEGORIES.has(raw as ToolCategory) ? (raw as ToolCategory) : "Unknown";
}

// A single catalog entry. requiresApproval is the catalog DEFAULT (MCP tools default ON); a bound agent
// definition may override it per-tool via its toolApprovals map. Names are the qualified executable names
// (MCP tools are namespaced mcp__{server}__{tool}) so they can be referenced directly by a definition.
// category is the tool's risk class; effectiveRequiresApproval is whether the CURRENT node policy gates it (the
// agent-independent floor an operator sees — a bound agent may tighten further, never loosen it).
export interface ToolCatalogEntry {
	readonly name: string;
	readonly description: string;
	readonly requiresApproval: boolean;
	readonly source: ToolCatalogSource;
	readonly category: ToolCategory;
	readonly effectiveRequiresApproval: boolean;
}

const MCP_NAME_PREFIX = "mcp__";
const MCP_NAME_DELIMITER = "__";

// Short, human-friendly tool name for display. MCP tool names are the qualified executable form
// mcp__{serverSlug}__{tool}; the originating server is already shown by the source badge, so the bare tool
// segment is enough in the row label. The qualified name is still used for keys/test ids and as the value a
// definition references — this only affects what the user reads. Non-MCP names (and any unexpected shape)
// pass through unchanged. The tool segment is everything after the serverSlug delimiter, so a tool name that
// itself contains "__" is preserved.
export function toToolDisplayName(name: string): string {
	if (!name.startsWith(MCP_NAME_PREFIX)) {
		return name;
	}
	const afterPrefix = name.slice(MCP_NAME_PREFIX.length);
	const delimiterIndex = afterPrefix.indexOf(MCP_NAME_DELIMITER);
	if (delimiterIndex <= 0) {
		return name;
	}
	const toolSegment = afterPrefix.slice(delimiterIndex + MCP_NAME_DELIMITER.length);
	return toolSegment.length > 0 ? toolSegment : name;
}
