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

// Graded reasoning efforts, offered for models that advertise the Ollama `thinking` capability.
export const reasoningEfforts: readonly ReasoningEffort[] = ["none", "low", "medium", "high"];
// Binary reasoning efforts (On/Off), offered for models WITHOUT the `thinking` capability that still reason by
// default (e.g. some GGUF chat templates). "on" lets the model's built-in reasoning run (think omitted); "none"
// suppresses it (think:false). "on" is first so it is the safe fallback default when a stale graded effort is
// clamped onto a binary model — restoring the model's natural reasoning, which the user can then switch off.
export const binaryReasoningEfforts: readonly ReasoningEffort[] = ["on", "none"];
// Every persistable reasoning-effort value — used to validate the hydrated localStorage value and persisted
// wire values so a binary "on" survives reload/round-trip instead of being narrowed away.
export const persistableReasoningEfforts: readonly ReasoningEffort[] = ["none", "on", "low", "medium", "high"];

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
		setShowTokensPerSecond: (value: boolean) => void;
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

export const useNodeChatPreferencesStore = create<NodeChatPreferencesStore>()((set) => ({
	selectedModel: readStoredModel(),
	reasoningEffort: readStoredReasoningEffort(),
	toolsEnabled: readStoredToolsEnabled(),
	sidebarCollapsed: readStoredSidebarCollapsed(),
	selectedConversationId: readStoredSelectedConversationId(),
	agentModeEnabled: readStoredAgentModeEnabled(),
	selectedAgentId: readStoredSelectedAgentId(),
	showTokensPerSecond: readStoredShowTokensPerSecond(),
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
		setShowTokensPerSecond: (value) => {
			writeStoredValue(SHOW_TOKENS_PER_SECOND_STORAGE_KEY, String(value));
			set({ showTokensPerSecond: value });
		},
	},
}));
