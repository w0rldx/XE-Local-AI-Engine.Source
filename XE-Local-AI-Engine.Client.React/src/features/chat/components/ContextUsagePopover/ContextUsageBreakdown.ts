import type { ContextWindowSnapshot, ContextWindowTool } from "@/features/chat/models/ContextWindowModels";

export type ContextSectionKey =
	| "systemPrompt"
	| "instructions"
	| "tools"
	| "knowledge"
	| "attachments"
	| "compaction"
	| "conversation"
	// The provider-reported input, used only when the snapshot carries no per-category estimate.
	| "used"
	| "reservedOutput"
	| "safetyMargin"
	| "free";

export interface ContextSection {
	key: ContextSectionKey;
	tokens: number;
	// Share of `windowTokens`, 0-100.
	percent: number;
}

export type ContextSource = "reported" | "estimated" | "unknown";

export function contextSource(snapshot: ContextWindowSnapshot | undefined): ContextSource {
	if (snapshot?.providerInputTokens != null) {
		return "reported";
	}
	return snapshot?.estimated ? "estimated" : "unknown";
}

// The tokens the request occupied: provider-reported when the round reported them, else the estimate's total.
function usedTokens(snapshot: ContextWindowSnapshot): number | undefined {
	return snapshot.providerInputTokens ?? snapshot.estimated?.totalTokens ?? undefined;
}

// A window of 0 means the node does not know the capacity; it renders as unknown, never as an empty bar.
export function hasKnownWindow(snapshot: ContextWindowSnapshot): boolean {
	return snapshot.windowTokens > 0;
}

export function remainingTokens(snapshot: ContextWindowSnapshot): number | undefined {
	const used = usedTokens(snapshot);
	return hasKnownWindow(snapshot) && used !== undefined ? Math.max(0, snapshot.usableWindowTokens - used) : undefined;
}

// A provider prefix ("ext:") is stripped only when a connection/model path follows it, so "qwen3:4b" keeps its tag.
const providerPrefix = /^[a-z][a-z0-9_-]*:(?=.*\/)/i;

function sameModel(a: string, b: string): boolean {
	const left = a.toLowerCase().replace(providerPrefix, "");
	const right = b.toLowerCase().replace(providerPrefix, "");
	return left === right || left.endsWith(`/${right}`) || right.endsWith(`/${left}`);
}

// Whether a last-round snapshot describes the selected model. An empty selection (the runtime default) or a snapshot
// without a served model id cannot be contradicted, so both count as a match.
export function isSnapshotForModel(snapshot: ContextWindowSnapshot, modelName: string): boolean {
	return modelName.length === 0 || !snapshot.modelId || sameModel(snapshot.modelId, modelName);
}

// Ordered bar sections, zero sections omitted. Categories come from the estimate; without one the reported input is a
// single "used" section. Reserve, margin and free complete the window.
export function computeContextSections(snapshot: ContextWindowSnapshot): ContextSection[] {
	const estimated = snapshot.estimated;
	const parts: [ContextSectionKey, number][] = estimated
		? [
				["systemPrompt", estimated.systemPromptTokens],
				["instructions", estimated.instructionsTokens],
				["tools", estimated.toolSchemaTokens + estimated.toolTemplatePreambleTokens],
				["knowledge", estimated.knowledgeTokens],
				["attachments", estimated.attachmentTokens],
				["compaction", estimated.compactionTokens],
				["conversation", estimated.conversationTokens],
			]
		: [["used", snapshot.providerInputTokens ?? 0]];
	// Free is measured against the same basis as the categories so the bar fills exactly the window: the estimate's
	// total when there is one, else the reported input. The "Remaining" row reports the truth (`remainingTokens`).
	const occupied = estimated ? estimated.totalTokens : (snapshot.providerInputTokens ?? 0);
	const free = hasKnownWindow(snapshot) ? Math.max(0, snapshot.usableWindowTokens - occupied) : 0;
	parts.push(["reservedOutput", snapshot.reservedOutputTokens], ["safetyMargin", snapshot.safetyMarginTokens], ["free", free]);

	const window = snapshot.windowTokens;
	return parts
		.filter(([, tokens]) => tokens > 0)
		.map(([key, tokens]) => ({ key, tokens, percent: window > 0 ? (tokens / window) * 100 : 0 }));
}

export type ToolGroupKind = "builtIn" | "mcp" | "custom" | "skills";

export interface ToolGroup {
	kind: ToolGroupKind;
	// The MCP server slug for an "mcp" group; undefined otherwise.
	server?: string;
	tools: { name: string; displayName: string; tokens: number }[];
	tokens: number;
}

const skillToolNames = new Set(["load_skill", "read_skill_resource", "run_skill_script"]);
const mcpPattern = /^mcp__(.+?)__(.+)$/;
const customPrefix = "custom__";
const kindOrder: ToolGroupKind[] = ["builtIn", "mcp", "custom", "skills"];

function classify(tool: ContextWindowTool): { kind: ToolGroupKind; server?: string; displayName: string } {
	const mcp = mcpPattern.exec(tool.name);
	if (mcp?.[1] && mcp[2]) {
		return { kind: "mcp", server: mcp[1], displayName: mcp[2] };
	}
	if (tool.name.startsWith(customPrefix)) {
		return { kind: "custom", displayName: tool.name.slice(customPrefix.length) };
	}
	return { kind: skillToolNames.has(tool.name) ? "skills" : "builtIn", displayName: tool.name };
}

// Built-in, then one group per MCP server (by slug), then custom, then skills; tools keep their sent order.
export function groupTools(tools: readonly ContextWindowTool[]): ToolGroup[] {
	const groups = new Map<string, ToolGroup>();
	for (const tool of tools) {
		const { kind, server, displayName } = classify(tool);
		const id = `${kind}:${server ?? ""}`;
		const group = groups.get(id) ?? { kind, server, tools: [], tokens: 0 };
		group.tools.push({ name: tool.name, displayName, tokens: tool.tokens });
		group.tokens += tool.tokens;
		groups.set(id, group);
	}
	return [...groups.values()].sort(
		(a, b) => kindOrder.indexOf(a.kind) - kindOrder.indexOf(b.kind) || (a.server ?? "").localeCompare(b.server ?? ""),
	);
}
