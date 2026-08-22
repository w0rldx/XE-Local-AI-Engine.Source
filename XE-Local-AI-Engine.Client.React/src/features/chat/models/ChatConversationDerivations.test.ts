import { describe, expect, it } from "vitest";

import {
	inFlightAssistantMessageId,
	mergeSelectedConversation,
	stampVariantGroup,
	titleFromContent,
} from "@/features/chat/models/ChatConversationDerivations";
import type { ChatConversationModel, ChatMessageModel, MessageStatus } from "@/features/chat/models/ChatModels";

function message(id: string, role: ChatMessageModel["role"], status: MessageStatus = "completed"): ChatMessageModel {
	return {
		id,
		conversationId: "conversation-1",
		role,
		content: id,
		status,
		sortOrder: 0,
		createdAt: "2026-08-22T12:00:00.000Z",
	};
}

function conversation(id: string, messages: ChatMessageModel[] = []): ChatConversationModel {
	return {
		id,
		title: id,
		origin: "local",
		messages,
		createdAt: "2026-08-22T12:00:00.000Z",
		updatedAt: "2026-08-22T12:00:00.000Z",
	};
}

describe("mergeSelectedConversation", () => {
	it("returns the original list when no selected detail is available", () => {
		const conversations = [conversation("first")];

		expect(mergeSelectedConversation(conversations)).toBe(conversations);
	});

	it("prepends a selected conversation that is absent from the summary list", () => {
		const selected = conversation("selected", [message("assistant", "assistant")]);
		const existing = conversation("existing");

		expect(mergeSelectedConversation([existing], selected)).toEqual([selected, existing]);
	});

	it("replaces the matching summary in place without disturbing list order", () => {
		const first = conversation("first");
		const selected = conversation("selected", [message("assistant", "assistant")]);
		const last = conversation("last");

		const merged = mergeSelectedConversation([first, conversation("selected"), last], selected);

		expect(merged).toEqual([first, selected, last]);
		expect(merged[0]).toBe(first);
		expect(merged[2]).toBe(last);
	});
});

describe("inFlightAssistantMessageId", () => {
	it.each(["pending", "queued", "streaming"] as const)("recognizes an assistant with %s status", (status) => {
		expect(inFlightAssistantMessageId(conversation("conversation-1", [message("live", "assistant", status)]))).toBe("live");
	});

	it("returns the latest live assistant while ignoring user and terminal messages", () => {
		const messages = [
			message("first-live", "assistant", "pending"),
			message("user", "user", "streaming"),
			message("completed", "assistant", "completed"),
			message("latest-live", "assistant", "streaming"),
		];

		expect(inFlightAssistantMessageId(conversation("conversation-1", messages))).toBe("latest-live");
	});

	it("returns undefined when no assistant turn is live", () => {
		expect(
			inFlightAssistantMessageId(
				conversation("conversation-1", [message("user", "user", "pending"), message("done", "assistant", "completed")]),
			),
		).toBeUndefined();
	});
});

describe("titleFromContent", () => {
	it("normalizes whitespace without truncating a short title", () => {
		expect(titleFromContent("  A title\n\twith   spacing  ")).toBe("A title with spacing");
	});

	it("uses the placeholder title for blank content", () => {
		expect(titleFromContent(" \n\t ")).toBe("New conversation");
	});

	it("keeps exactly 48 normalized characters", () => {
		const content = "x".repeat(48);

		expect(titleFromContent(content)).toBe(content);
	});

	it("truncates longer content to 45 characters plus an ellipsis", () => {
		expect(titleFromContent("x".repeat(49))).toBe(`${"x".repeat(45)}…`);
	});
});

describe("stampVariantGroup", () => {
	it("groups the original and streamed variant while preserving unrelated messages", () => {
		const original = message("original", "assistant");
		const variant = message("variant", "assistant", "streaming");
		const unrelated = message("unrelated", "user");

		const stamped = stampVariantGroup(
			conversation("conversation-1", [unrelated, original, variant]),
			"original",
			"variant",
			"group",
		);

		expect(stamped.messages).toEqual([
			unrelated,
			{ ...original, variantGroupId: "group" },
			{ ...variant, variantGroupId: "group" },
		]);
		expect(stamped.messages[0]).toBe(unrelated);
	});

	it("does not overwrite the original message's authoritative group", () => {
		const original = { ...message("original", "assistant"), variantGroupId: "server-group" };
		const variant = message("variant", "assistant", "streaming");

		const stamped = stampVariantGroup(
			conversation("conversation-1", [original, variant]),
			"original",
			"variant",
			"synthetic-group",
		);

		expect(stamped.messages).toEqual([original, { ...variant, variantGroupId: "synthetic-group" }]);
		expect(stamped.messages[0]).toBe(original);
	});
});
