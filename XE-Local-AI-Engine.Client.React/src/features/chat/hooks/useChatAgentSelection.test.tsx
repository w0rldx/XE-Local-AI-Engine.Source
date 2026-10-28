// @vitest-environment jsdom

import { renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { useChatAgentSelection } from "@/features/chat/hooks/useChatAgentSelection";
import { useNodeChatPreferencesStore } from "@/features/chat/stores/NodeChatPreferencesStore";

vi.mock("@/core/agents/queries/useAgentDefinitions", () => ({
	useAgentDefinitions: () => ({
		data: [
			{ id: "bound-agent", name: "r4-web", description: "", kind: "Single", modelProfile: null, playbookEnabled: false },
			{ id: "picked-agent", name: "Picked", description: "", kind: "Single", modelProfile: null, playbookEnabled: false },
		],
	}),
}));

describe("useChatAgentSelection", () => {
	afterEach(() => {
		useNodeChatPreferencesStore.setState({ agentModeEnabled: false, selectedAgentId: "" });
	});

	// The server runs a send without an agent pick as the conversation's bound agent, so the control shows it.
	it("shows the conversation's bound agent when the composer picks none", () => {
		useNodeChatPreferencesStore.setState({ agentModeEnabled: false, selectedAgentId: "" });

		const { result } = renderHook(() => useChatAgentSelection(undefined, true, "bound-agent"));

		expect(result.current.agentModeEnabled).toBe(true);
		expect(result.current.selectedAgentId).toBe("bound-agent");
		expect(result.current.conversationAgentBound).toBe(true);
	});

	it("keeps the composer's own pick, which the send carries and the server prefers over the binding", () => {
		useNodeChatPreferencesStore.setState({ agentModeEnabled: true, selectedAgentId: "picked-agent" });

		const { result } = renderHook(() => useChatAgentSelection(undefined, true, "bound-agent"));

		expect(result.current.selectedAgentId).toBe("picked-agent");
	});

	it("falls back to the stored preference for an unbound conversation", () => {
		useNodeChatPreferencesStore.setState({ agentModeEnabled: false, selectedAgentId: "" });

		const { result } = renderHook(() => useChatAgentSelection(undefined, true, undefined));

		expect(result.current.agentModeEnabled).toBe(false);
		expect(result.current.selectedAgentId).toBe("");
		expect(result.current.conversationAgentBound).toBe(false);
	});
});
