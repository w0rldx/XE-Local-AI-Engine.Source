import { create } from "zustand";

import type { ReasoningEffort } from "@/features/chat/models/ChatModels";
import { localDefaultModelValue } from "@/features/chat/models/NodeChatModelSelection";

// Persisted chat composer selections, mirroring the platform ToolCallingStore (zustand + guarded
// globalThis.localStorage). Keys are global (not per-conversation) like the platform client. The raw
// persisted value is stored as-is; Chat.tsx validates the model against the live model list and the
// reasoning effort against the available set on read, falling back when a persisted value is stale.
const SELECTED_MODEL_STORAGE_KEY = "xe-node-chat-selected-model";
const REASONING_EFFORT_STORAGE_KEY = "xe-node-chat-reasoning-effort";
const TOOLS_ENABLED_STORAGE_KEY = "xe-node-chat-tools-enabled";
const SIDEBAR_COLLAPSED_STORAGE_KEY = "xe-node-chat-sidebar-collapsed";
const SELECTED_CONVERSATION_STORAGE_KEY = "xe-node-chat-selected-conversation";
const AGENT_MODE_ENABLED_STORAGE_KEY = "xe-node-chat-agent-mode";
const SELECTED_AGENT_STORAGE_KEY = "xe-node-chat-selected-agent";
const SHOW_TOKENS_PER_SECOND_STORAGE_KEY = "xe-node-chat-show-tokens-per-second";
const KNOWLEDGE_BASE_ENABLED_STORAGE_KEY = "xe-node-chat-knowledge-base-enabled";

// Graded reasoning efforts, offered for models that advertise the Ollama `thinking` capability.
// "auto" is APPENDED, never prepended: clampReasoningEffort falls back to `available[0]`, so a leading "auto" would
// silently become the fallback for every stale value that has no comparable rank.
export const reasoningEfforts: readonly ReasoningEffort[] = ["none", "low", "medium", "high", "auto"];
// Binary reasoning efforts (On/Off), offered for models WITHOUT the `thinking` capability that still reason by
// default (e.g. some GGUF chat templates). "on" lets the model's built-in reasoning run (think omitted); "none"
// suppresses it (think:false). "on" is first so it is the safe fallback default when a stale graded effort is
// clamped onto a binary model — restoring the model's natural reasoning, which the user can then switch off.
export const binaryReasoningEfforts: readonly ReasoningEffort[] = ["on", "none"];
// Full graded effort set for Codex/cloud models (OpenAI Responses API reasoning.effort vocabulary).
// "minimal" and "xhigh" are Codex-only — NEVER offered for Ollama models. The Chat.tsx model-switch clamp
// resets any carryover Codex effort (e.g. "xhigh") when the user switches back to an Ollama model.
export const codexReasoningEfforts: readonly ReasoningEffort[] = ["none", "minimal", "low", "medium", "high", "xhigh", "auto"];
// Every persistable reasoning-effort value — used to validate the hydrated localStorage value and persisted
// wire values so a binary "on" survives reload/round-trip instead of being narrowed away.
export const persistableReasoningEfforts: readonly ReasoningEffort[] = [
	"none",
	"on",
	"minimal",
	"low",
	"medium",
	"high",
	"xhigh",
	"auto",
];

// Reasoning intensity rank, used to clamp a carried-over effort onto a different model's available set without
// collapsing reasoning intent. Codex-only "minimal"/"xhigh" sit at the extremes; "on" is the binary reasoning-ON
// sentinel and ranks alongside "low" so a binary "on" maps to a sensible graded level (and any graded-ON level
// maps back to "on"). "none" is the only reasoning-OFF value.
const reasoningEffortRank: Readonly<Record<ReasoningEffort, number>> = {
	none: 0,
	minimal: 1,
	low: 2,
	on: 2,
	medium: 3,
	high: 4,
	xhigh: 5,
	// The node picks the tier per turn, so "auto" has no intrinsic intensity. Ranked WITH "medium" so clamping it onto
	// a model that does not offer it degrades to the middle of the graded set (and to "on" on a binary model) rather
	// than collapsing to the list's first entry.
	auto: 3,
};

// Map an effort onto a target model's available set, preserving reasoning intent instead of always falling back to
// the set's first entry. Returns `current` unchanged when it is already valid (byte-identical no-op). Otherwise:
// a reasoning-OFF source ("none") maps to "none" when offered; any reasoning-ON source maps to the available
// reasoning-ON level (rank > 0) with the nearest intensity rank — so xhigh→high, minimal→low onto a graded set, and
// any graded level→"on" onto a binary set. Falls back to the set's first entry only when no comparable level exists.
export function clampReasoningEffort(
	current: ReasoningEffort,
	available: readonly ReasoningEffort[],
): ReasoningEffort {
	if (available.includes(current)) {
		return current;
	}

	const fallback = available[0] ?? "none";
	if (current === "none") {
		return available.includes("none") ? "none" : fallback;
	}

	const targetRank = reasoningEffortRank[current];
	let nearest: ReasoningEffort | undefined;
	let nearestDistance = Number.POSITIVE_INFINITY;
	for (const candidate of available) {
		if (reasoningEffortRank[candidate] === 0) {
			continue;
		}

		const distance = Math.abs(reasoningEffortRank[candidate] - targetRank);
		if (distance < nearestDistance) {
			nearest = candidate;
			nearestDistance = distance;
		}
	}

	return nearest ?? fallback;
}

interface NodeChatPreferencesStore {
	selectedModel: string;
	reasoningEffort: ReasoningEffort;
	toolsEnabled: boolean;
	sidebarCollapsed: boolean;
	selectedConversationId: string;
	// Agent mode: when enabled the composer shows the agent picker and stamps the selected agent on each send.
	// agentModeEnabled persists the toggle; selectedAgentId persists the last-chosen agent (may be stale if the
	// agent was deleted — Chat.tsx validates against the live list on read and drops stale ids).
	agentModeEnabled: boolean;
	selectedAgentId: string;
	// When enabled, completed assistant turns show a tokens/sec figure on their attribution line (computed from the
	// persisted generation duration + output tokens). Off by default; global preference like the others above.
	showTokensPerSecond: boolean;
	// When enabled (default OFF), a plain-chat send opts into knowledge-base grounding: the server retrieves the
	// top-k knowledge-base hits for the message and inlines them (fenced) into the turn, surfacing their sources.
	// Global preference stamped per-send, mirroring toolsEnabled. Ignored in agent mode (the agent uses the KB tool).
	knowledgeBaseEnabled: boolean;
	actions: {
		setSelectedModel: (value: string) => void;
		setReasoningEffort: (value: ReasoningEffort) => void;
		setToolsEnabled: (value: boolean) => void;
		toggleTools: () => void;
		setSidebarCollapsed: (value: boolean) => void;
		toggleSidebar: () => void;
		setSelectedConversationId: (value: string) => void;
		setAgentModeEnabled: (value: boolean) => void;
		toggleAgentMode: () => void;
		setSelectedAgentId: (value: string) => void;
		// Clears the agent selection when starting a fresh conversation so a previously-pinned agent
		// cannot silently carry over and interfere with a new thread's model selection.
		clearSelectedAgent: () => void;
		setShowTokensPerSecond: (value: boolean) => void;
		setKnowledgeBaseEnabled: (value: boolean) => void;
		toggleKnowledgeBase: () => void;
	};
}

function readStoredString(key: string): string | undefined {
	try {
		return globalThis.localStorage?.getItem(key) ?? undefined;
	} catch {
		return undefined;
	}
}

function writeStoredValue(key: string, value: string): void {
	try {
		globalThis.localStorage?.setItem(key, value);
	} catch {
		// Ignore unavailable storage or quota errors; the in-memory preference still updates.
	}
}

function readStoredModel(): string {
	const stored = readStoredString(SELECTED_MODEL_STORAGE_KEY);
	return stored && stored.trim().length > 0 ? stored : localDefaultModelValue;
}

function readStoredReasoningEffort(): ReasoningEffort {
	const stored = readStoredString(REASONING_EFFORT_STORAGE_KEY);
	return persistableReasoningEfforts.includes(stored as ReasoningEffort) ? (stored as ReasoningEffort) : "medium";
}

function readStoredToolsEnabled(): boolean {
	return readStoredString(TOOLS_ENABLED_STORAGE_KEY) === "true";
}

function readStoredSidebarCollapsed(): boolean {
	return readStoredString(SIDEBAR_COLLAPSED_STORAGE_KEY) === "true";
}

function readStoredSelectedConversationId(): string {
	return readStoredString(SELECTED_CONVERSATION_STORAGE_KEY) ?? "";
}

function readStoredAgentModeEnabled(): boolean {
	return readStoredString(AGENT_MODE_ENABLED_STORAGE_KEY) === "true";
}

function readStoredSelectedAgentId(): string {
	return readStoredString(SELECTED_AGENT_STORAGE_KEY) ?? "";
}

function readStoredShowTokensPerSecond(): boolean {
	return readStoredString(SHOW_TOKENS_PER_SECOND_STORAGE_KEY) === "true";
}

function readStoredKnowledgeBaseEnabled(): boolean {
	return readStoredString(KNOWLEDGE_BASE_ENABLED_STORAGE_KEY) === "true";
}

export const useNodeChatPreferencesStore = create<NodeChatPreferencesStore>()((set) => ({
	selectedModel: readStoredModel(),
	reasoningEffort: readStoredReasoningEffort(),
	toolsEnabled: readStoredToolsEnabled(),
	sidebarCollapsed: readStoredSidebarCollapsed(),
	selectedConversationId: readStoredSelectedConversationId(),
	agentModeEnabled: readStoredAgentModeEnabled(),
	selectedAgentId: readStoredSelectedAgentId(),
	showTokensPerSecond: readStoredShowTokensPerSecond(),
	knowledgeBaseEnabled: readStoredKnowledgeBaseEnabled(),
	actions: {
		setSelectedModel: (value) => {
			writeStoredValue(SELECTED_MODEL_STORAGE_KEY, value);
			set({ selectedModel: value });
		},
		setReasoningEffort: (value) => {
			writeStoredValue(REASONING_EFFORT_STORAGE_KEY, value);
			set({ reasoningEffort: value });
		},
		setToolsEnabled: (value) => {
			writeStoredValue(TOOLS_ENABLED_STORAGE_KEY, String(value));
			set({ toolsEnabled: value });
		},
		toggleTools: () => {
			set((state) => {
				const nextValue = !state.toolsEnabled;
				writeStoredValue(TOOLS_ENABLED_STORAGE_KEY, String(nextValue));

				return { toolsEnabled: nextValue };
			});
		},
		setSidebarCollapsed: (value) => {
			writeStoredValue(SIDEBAR_COLLAPSED_STORAGE_KEY, String(value));
			set({ sidebarCollapsed: value });
		},
		toggleSidebar: () => {
			set((state) => {
				const nextValue = !state.sidebarCollapsed;
				writeStoredValue(SIDEBAR_COLLAPSED_STORAGE_KEY, String(nextValue));

				return { sidebarCollapsed: nextValue };
			});
		},
		setSelectedConversationId: (value) => {
			writeStoredValue(SELECTED_CONVERSATION_STORAGE_KEY, value);
			set({ selectedConversationId: value });
		},
		setAgentModeEnabled: (value) => {
			writeStoredValue(AGENT_MODE_ENABLED_STORAGE_KEY, String(value));
			set({ agentModeEnabled: value });
		},
		toggleAgentMode: () => {
			set((state) => {
				const nextValue = !state.agentModeEnabled;
				writeStoredValue(AGENT_MODE_ENABLED_STORAGE_KEY, String(nextValue));

				return { agentModeEnabled: nextValue };
			});
		},
		setSelectedAgentId: (value) => {
			writeStoredValue(SELECTED_AGENT_STORAGE_KEY, value);
			set({ selectedAgentId: value });
		},
		clearSelectedAgent: () => {
			writeStoredValue(SELECTED_AGENT_STORAGE_KEY, "");
			set({ selectedAgentId: "" });
		},
		setShowTokensPerSecond: (value) => {
			writeStoredValue(SHOW_TOKENS_PER_SECOND_STORAGE_KEY, String(value));
			set({ showTokensPerSecond: value });
		},
		setKnowledgeBaseEnabled: (value) => {
			writeStoredValue(KNOWLEDGE_BASE_ENABLED_STORAGE_KEY, String(value));
			set({ knowledgeBaseEnabled: value });
		},
		toggleKnowledgeBase: () => {
			set((state) => {
				const nextValue = !state.knowledgeBaseEnabled;
				writeStoredValue(KNOWLEDGE_BASE_ENABLED_STORAGE_KEY, String(nextValue));

				return { knowledgeBaseEnabled: nextValue };
			});
		},
	},
}));
