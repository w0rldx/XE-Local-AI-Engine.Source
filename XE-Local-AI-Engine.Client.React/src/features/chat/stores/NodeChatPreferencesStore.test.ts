// @vitest-environment jsdom

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import {
	binaryReasoningEfforts,
	clampReasoningEffort,
	reasoningEfforts,
} from "@/features/chat/stores/NodeChatPreferencesStore";

const SELECTED_MODEL_STORAGE_KEY = "xe-node-chat-selected-model";
const REASONING_EFFORT_STORAGE_KEY = "xe-node-chat-reasoning-effort";
const TOOLS_ENABLED_STORAGE_KEY = "xe-node-chat-tools-enabled";
const SIDEBAR_COLLAPSED_STORAGE_KEY = "xe-node-chat-sidebar-collapsed";
const SELECTED_CONVERSATION_STORAGE_KEY = "xe-node-chat-selected-conversation";
const AGENT_MODE_ENABLED_STORAGE_KEY = "xe-node-chat-agent-mode";
const SELECTED_AGENT_STORAGE_KEY = "xe-node-chat-selected-agent";
const SHOW_TOKENS_PER_SECOND_STORAGE_KEY = "xe-node-chat-show-tokens-per-second";

// The store reads localStorage once at module-init, so each test seeds storage then re-imports the module
// with a fresh registry to exercise the init path.
async function loadStore(seed: Record<string, string> = {}) {
	localStorage.clear();
	for (const [key, value] of Object.entries(seed)) {
		localStorage.setItem(key, value);
	}

	vi.resetModules();
	const module = await import("@/features/chat/stores/NodeChatPreferencesStore");
	return module.useNodeChatPreferencesStore;
}

describe("NodeChatPreferencesStore", () => {
	beforeEach(() => {
		localStorage.clear();
	});

	afterEach(() => {
		localStorage.clear();
	});

	it("falls back to defaults when nothing is persisted", async () => {
		const useStore = await loadStore();
		const state = useStore.getState();

		expect(state.selectedModel).toBe("local-default");
		expect(state.reasoningEffort).toBe("medium");
		expect(state.toolsEnabled).toBe(false);
		expect(state.sidebarCollapsed).toBe(false);
		expect(state.selectedConversationId).toBe("");
	});

	it("hydrates persisted selections on init", async () => {
		const useStore = await loadStore({
			[SELECTED_MODEL_STORAGE_KEY]: "llama3:8b",
			[REASONING_EFFORT_STORAGE_KEY]: "high",
			[TOOLS_ENABLED_STORAGE_KEY]: "true",
			[SIDEBAR_COLLAPSED_STORAGE_KEY]: "true",
			[SELECTED_CONVERSATION_STORAGE_KEY]: "conv-123",
		});
		const state = useStore.getState();

		expect(state.selectedModel).toBe("llama3:8b");
		expect(state.reasoningEffort).toBe("high");
		expect(state.toolsEnabled).toBe(true);
		expect(state.sidebarCollapsed).toBe(true);
		expect(state.selectedConversationId).toBe("conv-123");
	});

	it("falls back to medium when the persisted reasoning effort is not in the available set", async () => {
		const useStore = await loadStore({ [REASONING_EFFORT_STORAGE_KEY]: "ludicrous" });

		expect(useStore.getState().reasoningEffort).toBe("medium");
	});

	it("hydrates a persisted binary 'on' reasoning effort (a valid persistable value)", async () => {
		const useStore = await loadStore({ [REASONING_EFFORT_STORAGE_KEY]: "on" });

		expect(useStore.getState().reasoningEffort).toBe("on");
	});

	it("keeps the graded + binary + codex effort lists in sync with the persistable validation set", async () => {
		// persistableReasoningEfforts is the validation source of truth for both store hydrate and the wire mapper, so
		// it must equal the union of all three menu lists (Ollama graded, binary, Codex) — otherwise a value offered in
		// the UI could be silently dropped on reload/round-trip. Pins the four hand-maintained lists against desync.
		const { reasoningEfforts, binaryReasoningEfforts, codexReasoningEfforts, persistableReasoningEfforts } = await import(
			"@/features/chat/stores/NodeChatPreferencesStore"
		);

		expect(new Set(persistableReasoningEfforts)).toEqual(
			new Set([...reasoningEfforts, ...binaryReasoningEfforts, ...codexReasoningEfforts]),
		);
	});

	it("hydrates a persisted Codex-only 'minimal' reasoning effort (a valid persistable value)", async () => {
		const useStore = await loadStore({ [REASONING_EFFORT_STORAGE_KEY]: "minimal" });

		expect(useStore.getState().reasoningEffort).toBe("minimal");
	});

	it("hydrates a persisted Codex-only 'xhigh' reasoning effort (a valid persistable value)", async () => {
		const useStore = await loadStore({ [REASONING_EFFORT_STORAGE_KEY]: "xhigh" });

		expect(useStore.getState().reasoningEffort).toBe("xhigh");
	});

	it("codexReasoningEfforts contains minimal and xhigh but Ollama lists do not", async () => {
		// Guard that Codex-only levels never leak into the Ollama pickers.
		const { reasoningEfforts, binaryReasoningEfforts, codexReasoningEfforts } = await import(
			"@/features/chat/stores/NodeChatPreferencesStore"
		);

		expect(codexReasoningEfforts).toContain("minimal");
		expect(codexReasoningEfforts).toContain("xhigh");
		expect(reasoningEfforts).not.toContain("minimal");
		expect(reasoningEfforts).not.toContain("xhigh");
		expect(binaryReasoningEfforts).not.toContain("minimal");
		expect(binaryReasoningEfforts).not.toContain("xhigh");
	});

	it("persists selections to localStorage when set", async () => {
		const useStore = await loadStore();

		useStore.getState().actions.setSelectedModel("qwen2:7b");
		useStore.getState().actions.setReasoningEffort("low");
		useStore.getState().actions.setToolsEnabled(true);

		expect(localStorage.getItem(SELECTED_MODEL_STORAGE_KEY)).toBe("qwen2:7b");
		expect(localStorage.getItem(REASONING_EFFORT_STORAGE_KEY)).toBe("low");
		expect(localStorage.getItem(TOOLS_ENABLED_STORAGE_KEY)).toBe("true");
	});

	it("toggles tools and persists the flipped value", async () => {
		const useStore = await loadStore({ [TOOLS_ENABLED_STORAGE_KEY]: "true" });

		useStore.getState().actions.toggleTools();

		expect(useStore.getState().toolsEnabled).toBe(false);
		expect(localStorage.getItem(TOOLS_ENABLED_STORAGE_KEY)).toBe("false");
	});

	it("persists the sidebar collapsed state when set and toggled", async () => {
		const useStore = await loadStore();

		useStore.getState().actions.setSidebarCollapsed(true);
		expect(useStore.getState().sidebarCollapsed).toBe(true);
		expect(localStorage.getItem(SIDEBAR_COLLAPSED_STORAGE_KEY)).toBe("true");

		useStore.getState().actions.toggleSidebar();
		expect(useStore.getState().sidebarCollapsed).toBe(false);
		expect(localStorage.getItem(SIDEBAR_COLLAPSED_STORAGE_KEY)).toBe("false");
	});

	it("persists the last-selected conversation id when set", async () => {
		const useStore = await loadStore();

		useStore.getState().actions.setSelectedConversationId("conv-789");
		expect(useStore.getState().selectedConversationId).toBe("conv-789");
		expect(localStorage.getItem(SELECTED_CONVERSATION_STORAGE_KEY)).toBe("conv-789");

		useStore.getState().actions.setSelectedConversationId("");
		expect(useStore.getState().selectedConversationId).toBe("");
		expect(localStorage.getItem(SELECTED_CONVERSATION_STORAGE_KEY)).toBe("");
	});

	it("defaults agent mode to off and selected agent to empty", async () => {
		const useStore = await loadStore();
		const state = useStore.getState();

		expect(state.agentModeEnabled).toBe(false);
		expect(state.selectedAgentId).toBe("");
	});

	it("hydrates agent mode and selected agent from localStorage on init", async () => {
		const useStore = await loadStore({
			[AGENT_MODE_ENABLED_STORAGE_KEY]: "true",
			[SELECTED_AGENT_STORAGE_KEY]: "agent-abc",
		});
		const state = useStore.getState();

		expect(state.agentModeEnabled).toBe(true);
		expect(state.selectedAgentId).toBe("agent-abc");
	});

	it("persists agent mode when set", async () => {
		const useStore = await loadStore();

		useStore.getState().actions.setAgentModeEnabled(true);
		expect(useStore.getState().agentModeEnabled).toBe(true);
		expect(localStorage.getItem(AGENT_MODE_ENABLED_STORAGE_KEY)).toBe("true");

		useStore.getState().actions.setAgentModeEnabled(false);
		expect(useStore.getState().agentModeEnabled).toBe(false);
		expect(localStorage.getItem(AGENT_MODE_ENABLED_STORAGE_KEY)).toBe("false");
	});

	it("toggles agent mode and persists the flipped value", async () => {
		const useStore = await loadStore({ [AGENT_MODE_ENABLED_STORAGE_KEY]: "false" });

		useStore.getState().actions.toggleAgentMode();

		expect(useStore.getState().agentModeEnabled).toBe(true);
		expect(localStorage.getItem(AGENT_MODE_ENABLED_STORAGE_KEY)).toBe("true");
	});

	it("persists the selected agent id when set", async () => {
		const useStore = await loadStore();

		useStore.getState().actions.setSelectedAgentId("agent-xyz");
		expect(useStore.getState().selectedAgentId).toBe("agent-xyz");
		expect(localStorage.getItem(SELECTED_AGENT_STORAGE_KEY)).toBe("agent-xyz");

		useStore.getState().actions.setSelectedAgentId("");
		expect(useStore.getState().selectedAgentId).toBe("");
		expect(localStorage.getItem(SELECTED_AGENT_STORAGE_KEY)).toBe("");
	});

	it("clearSelectedAgent resets the agent selection and clears localStorage", async () => {
		// Simulate a previously-selected agent persisted from a prior conversation.
		const useStore = await loadStore({ [SELECTED_AGENT_STORAGE_KEY]: "agent-from-prior-convo" });

		expect(useStore.getState().selectedAgentId).toBe("agent-from-prior-convo");

		useStore.getState().actions.clearSelectedAgent();

		expect(useStore.getState().selectedAgentId).toBe("");
		expect(localStorage.getItem(SELECTED_AGENT_STORAGE_KEY)).toBe("");
	});

	it("defaults the tokens/sec toggle to off", async () => {
		const useStore = await loadStore();

		expect(useStore.getState().showTokensPerSecond).toBe(false);
	});

	it("hydrates the persisted tokens/sec toggle on init", async () => {
		const useStore = await loadStore({ [SHOW_TOKENS_PER_SECOND_STORAGE_KEY]: "true" });

		expect(useStore.getState().showTokensPerSecond).toBe(true);
	});

	it("persists the tokens/sec toggle when set", async () => {
		const useStore = await loadStore();

		useStore.getState().actions.setShowTokensPerSecond(true);
		expect(useStore.getState().showTokensPerSecond).toBe(true);
		expect(localStorage.getItem(SHOW_TOKENS_PER_SECOND_STORAGE_KEY)).toBe("true");

		useStore.getState().actions.setShowTokensPerSecond(false);
		expect(useStore.getState().showTokensPerSecond).toBe(false);
		expect(localStorage.getItem(SHOW_TOKENS_PER_SECOND_STORAGE_KEY)).toBe("false");
	});
});

describe("clampReasoningEffort", () => {
	// reasoningEfforts = ["none", "low", "medium", "high", "auto"] (graded Ollama set).
	// binaryReasoningEfforts = ["on", "none"].

	it("returns the current effort unchanged when it is already in the available set", () => {
		expect(clampReasoningEffort("medium", reasoningEfforts)).toBe("medium");
		expect(clampReasoningEffort("none", reasoningEfforts)).toBe("none");
		expect(clampReasoningEffort("on", binaryReasoningEfforts)).toBe("on");
	});

	it("maps Codex-only 'xhigh' onto a graded set's nearest reasoning-ON level (high)", () => {
		expect(clampReasoningEffort("xhigh", reasoningEfforts)).toBe("high");
	});

	it("maps Codex-only 'minimal' onto a graded set's nearest reasoning-ON level (low)", () => {
		expect(clampReasoningEffort("minimal", reasoningEfforts)).toBe("low");
	});

	it("preserves 'none' when switching onto a graded set (reasoning stays off)", () => {
		expect(clampReasoningEffort("none", reasoningEfforts)).toBe("none");
	});

	// A binary model has no graded ladder for the node to dispatch into, so a stale stored "auto" reaching one must
	// degrade to the model's natural reasoning rather than collapsing to the list's first entry by rank accident.
	it("maps a stale 'auto' onto a binary set's 'on'", () => {
		expect(clampReasoningEffort("auto", binaryReasoningEfforts)).toBe("on");
	});

	it("maps any reasoning-ON graded level onto a binary set's 'on'", () => {
		expect(clampReasoningEffort("high", binaryReasoningEfforts)).toBe("on");
		expect(clampReasoningEffort("medium", binaryReasoningEfforts)).toBe("on");
		expect(clampReasoningEffort("low", binaryReasoningEfforts)).toBe("on");
	});

	it("maps Codex-only levels onto a binary set's 'on'", () => {
		expect(clampReasoningEffort("xhigh", binaryReasoningEfforts)).toBe("on");
		expect(clampReasoningEffort("minimal", binaryReasoningEfforts)).toBe("on");
	});

	it("preserves 'none' when switching onto a binary set", () => {
		expect(clampReasoningEffort("none", binaryReasoningEfforts)).toBe("none");
	});

	it("falls back to the first available effort when the set offers no reasoning-ON level", () => {
		expect(clampReasoningEffort("high", ["none"])).toBe("none");
	});
});
