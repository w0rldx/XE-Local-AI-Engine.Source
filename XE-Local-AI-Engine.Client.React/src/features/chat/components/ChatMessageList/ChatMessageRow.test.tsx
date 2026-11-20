// @vitest-environment jsdom

import { cleanup, render } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { ChatMessageRow } from "@/features/chat/components/ChatMessageList/ChatMessageRow";
import type { ChatMessageModel, ChatMessageRevisionNav } from "@/features/chat/models/ChatModels";
import type { MessageRevisionGroup } from "@/features/chat/models/MessageRevisionGrouping";

const received: (ChatMessageRevisionNav | undefined)[] = [];

// ChatMessage is memoized; the row only has to hand it the same props. Recording them is the whole check here.
vi.mock("@/features/chat/components/ChatMessage", () => ({
	ChatMessage: ({ revisionNav }: { revisionNav?: ChatMessageRevisionNav }) => {
		received.push(revisionNav);
		return null;
	},
}));

function revision(id: string, sortOrder: number): ChatMessageModel {
	return {
		id,
		conversationId: "conversation-1",
		role: "assistant",
		content: `answer ${id}`,
		status: "completed",
		createdAt: "2026-06-04T00:00:00.000Z",
		sortOrder,
		variantGroupId: "group-1",
	};
}

describe("ChatMessageRow revision navigation", () => {
	afterEach(() => {
		cleanup();
		received.length = 0;
	});

	it("hands a settled turn the same revisionNav on every re-render while another turn streams", () => {
		const first = revision("a-1", 2);
		const second = revision("a-2", 2);
		const group: MessageRevisionGroup = { active: second, revisions: [first, second], activeIndex: 1 };
		const onSelectRevision = vi.fn();
		const row = (content: string) => (
			<ChatMessageRow
				row={{ kind: "group", key: second.id, group }}
				scopedStreamingMessage={{ conversationId: "conversation-1", messageId: "other", content, isActive: true }}
				hasStreamingContent={content.length > 0}
				normalizedMessageCount={3}
				onSelectRevision={onSelectRevision}
				showFeedbackControls={false}
				isWorkSessionConversation={false}
				isPlainConversation={false}
				showTurnThroughput={false}
			/>
		);

		const view = render(row("The"));
		view.rerender(row("The answer"));

		expect(received).toHaveLength(2);
		expect(received[0]).toBeDefined();
		expect(received[1]).toBe(received[0]);
		received[0]?.onPrevious();
		expect(onSelectRevision).toHaveBeenCalledWith("group-1", "a-1");
	});
});
