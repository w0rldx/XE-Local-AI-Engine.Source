// @vitest-environment jsdom

import { beforeEach, describe, expect, it } from "vitest";

import { usePendingChatConversationStore } from "@/core/ui/stores/PendingChatConversationStore";

const conversationId = "33333333-0000-4000-8000-000000000001";

describe("usePendingChatConversationStore", () => {
	beforeEach(() => {
		usePendingChatConversationStore.setState({ pendingConversationId: "" });
	});

	it("hands the staged conversation back and empties itself", () => {
		usePendingChatConversationStore.getState().actions.setPendingConversationId(conversationId);

		expect(usePendingChatConversationStore.getState().actions.consume()).toBe(conversationId);
		expect(usePendingChatConversationStore.getState().pendingConversationId).toBe("");
	});

	// Chat consumes from a mount effect: a second consume must be a no-op, or a remount would re-select a
	// conversation the operator has since navigated away from.
	it("returns an empty string once consumed", () => {
		usePendingChatConversationStore.getState().actions.setPendingConversationId(conversationId);
		usePendingChatConversationStore.getState().actions.consume();

		expect(usePendingChatConversationStore.getState().actions.consume()).toBe("");
	});
});
