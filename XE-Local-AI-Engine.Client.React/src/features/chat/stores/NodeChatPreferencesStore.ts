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

// Single source of truth for the selectable reasoning efforts: the hydrate-validation set here and the
// composer's availableReasoningEfforts prop both read from this list.
export const reasoningEfforts: readonly ReasoningEffort[] = ["none", "low", "medium", "high"];

interface NodeChatPreferencesStore {
	selectedModel: string;
	reasoningEffort: ReasoningEffort;
	toolsEnabled: boolean;
	sidebarCollapsed: boolean;
	selectedConversationId: string;
	actions: {
		setSelectedModel: (value: string) => void;
		setReasoningEffort: (value: ReasoningEffort) => void;
		setToolsEnabled: (value: boolean) => void;
		toggleTools: () => void;
		setSidebarCollapsed: (value: boolean) => void;
		toggleSidebar: () => void;
		setSelectedConversationId: (value: string) => void;
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
	return reasoningEfforts.includes(stored as ReasoningEffort) ? (stored as ReasoningEffort) : "medium";
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

export const useNodeChatPreferencesStore = create<NodeChatPreferencesStore>()((set) => ({
	selectedModel: readStoredModel(),
	reasoningEffort: readStoredReasoningEffort(),
	toolsEnabled: readStoredToolsEnabled(),
	sidebarCollapsed: readStoredSidebarCollapsed(),
	selectedConversationId: readStoredSelectedConversationId(),
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
	},
}));
