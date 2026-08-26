// @vitest-environment jsdom

import { act, renderHook, waitFor } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { useChatRevisionSelection } from "@/features/chat/hooks/useChatRevisionSelection";
import type { ChatConversationModel } from "@/features/chat/models/ChatModels";

function conversation(id: string, selectedPath: Record<string, string>): ChatConversationModel {
	return { id, selectedPath } as ChatConversationModel;
}

describe("useChatRevisionSelection", () => {
	it("keeps the first loaded baseline and layers in-session selections over it", async () => {
		const first = conversation("conversation-1", { groupA: "message-a" });
		const { result, rerender } = renderHook(({ loaded }) => useChatRevisionSelection("conversation-1", loaded), {
			initialProps: { loaded: first },
		});

		await waitFor(() => expect(result.current.activeRevisionByGroup).toEqual({ groupA: "message-a" }));
		act(() => {
			expect(result.current.selectRevision("groupB", "message-b")).toEqual({
				groupA: "message-a",
				groupB: "message-b",
			});
		});

		rerender({ loaded: conversation("conversation-1", { groupA: "background-refetch" }) });
		expect(result.current.activeRevisionByGroup).toEqual({ groupA: "message-a", groupB: "message-b" });
	});

	it("does not leak overrides to another conversation", async () => {
		const { result, rerender } = renderHook(({ id, loaded }) => useChatRevisionSelection(id, loaded), {
			initialProps: { id: "conversation-1", loaded: conversation("conversation-1", { groupA: "message-a" }) },
		});
		act(() => result.current.selectRevision("groupB", "message-b"));

		rerender({ id: "conversation-2", loaded: conversation("conversation-2", { groupC: "message-c" }) });
		await waitFor(() => expect(result.current.activeRevisionByGroup).toEqual({ groupC: "message-c" }));
	});
});
