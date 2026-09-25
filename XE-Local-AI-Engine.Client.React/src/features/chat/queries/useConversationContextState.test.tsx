// @vitest-environment jsdom

import { renderHook, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

const { getSpy } = vi.hoisted(() => ({ getSpy: vi.fn() }));

vi.mock("@/features/chat/api/NodeChatAdapter", () => ({
	nodeChatAdapter: { getConversationContextState: getSpy },
}));

import { nodeChatQueryKeys } from "@/features/chat/queries/NodeChatQueryKeys";
import { useConversationContextState } from "@/features/chat/queries/useConversationContextState";
import { createProvidersWrapper } from "@/test/RenderWithProviders";

describe("useConversationContextState", () => {
	afterEach(() => {
		vi.clearAllMocks();
	});

	it("loads through the adapter and caches under the conversation's context-state key", async () => {
		const state = { entries: [], synopsis: "s" };
		getSpy.mockResolvedValue(state);
		const { wrapper, queryClient } = createProvidersWrapper();

		const { result } = renderHook(() => useConversationContextState("conv-1", { enabled: true }), { wrapper });

		await waitFor(() => expect(result.current.data).toEqual(state));
		expect(getSpy).toHaveBeenCalledWith("conv-1", expect.objectContaining({ signal: expect.any(AbortSignal) }));
		expect(queryClient.getQueryData(nodeChatQueryKeys.conversationContextState("conv-1"))).toEqual(state);
		expect(nodeChatQueryKeys.conversationContextState("conv-1")).toEqual([
			"node-chat",
			"conversations",
			"conv-1",
			"context-state",
		]);
	});

	it("does not fetch while disabled (drawer closed)", () => {
		const { wrapper } = createProvidersWrapper();

		renderHook(() => useConversationContextState("conv-1", { enabled: false }), { wrapper });

		expect(getSpy).not.toHaveBeenCalled();
	});
});
