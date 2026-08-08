import { describe, expect, it } from "vitest";

import type { ChatMessageModel } from "@/features/chat/models/ChatModels";
import { groupMessageRevisions } from "@/features/chat/models/MessageRevisionGrouping";

function message(overrides: Partial<ChatMessageModel> = {}): ChatMessageModel {
	return {
		id: "message-1",
		conversationId: "conversation-1",
		role: "assistant",
		content: "content",
		status: "completed",
		createdAt: "2026-05-24T00:00:00.000Z",
		sortOrder: 0,
		...overrides,
	};
}

describe("groupMessageRevisions", () => {
	it("passes through messages without a variant group as singletons", () => {
		const messages = [
			message({ id: "user-1", role: "user", sortOrder: 0 }),
			message({ id: "assistant-1", sortOrder: 1 }),
		];

		const groups = groupMessageRevisions(messages);

		expect(groups).toHaveLength(2);
		expect(groups.map((group) => group.active.id)).toEqual(["user-1", "assistant-1"]);
		expect(groups.every((group) => group.revisions.length === 1)).toBe(true);
	});

	it("collapses sibling variants into a single group and defaults to the newest revision", () => {
		const messages = [
			message({ id: "user-1", role: "user", sortOrder: 0 }),
			message({ id: "v1", variantGroupId: "group-a", sortOrder: 1 }),
			message({ id: "v2", variantGroupId: "group-a", sortOrder: 2 }),
			message({ id: "v3", variantGroupId: "group-a", sortOrder: 3 }),
		];

		const groups = groupMessageRevisions(messages);

		expect(groups).toHaveLength(2);
		const variantGroup = groups.at(1);
		expect(variantGroup?.revisions.map((revision) => revision.id)).toEqual(["v1", "v2", "v3"]);
		expect(variantGroup?.active.id).toBe("v3");
		expect(variantGroup?.activeIndex).toBe(2);
	});

	it("honors an explicit active-revision selection per group", () => {
		const messages = [
			message({ id: "v1", variantGroupId: "group-a", sortOrder: 1 }),
			message({ id: "v2", variantGroupId: "group-a", sortOrder: 2 }),
		];

		const groups = groupMessageRevisions(messages, { "group-a": "v1" });

		expect(groups.at(0)?.active.id).toBe("v1");
		expect(groups.at(0)?.activeIndex).toBe(0);
	});

	it("anchors the collapsed group at the earliest variant's position", () => {
		const messages = [
			message({ id: "v1", variantGroupId: "group-a", sortOrder: 1 }),
			message({ id: "later", sortOrder: 2 }),
			message({ id: "v2", variantGroupId: "group-a", sortOrder: 3 }),
		];

		const groups = groupMessageRevisions(messages);

		expect(groups.map((group) => group.active.id)).toEqual(["v2", "later"]);
		expect(groups.at(0)?.revisions).toHaveLength(2);
	});
});
