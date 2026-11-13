import { beforeEach, describe, expect, it, vi } from "vitest";

// Capture the generated SDK call so the test sees the body the node receives.
const setSamplingMock = vi.hoisted(() => vi.fn());

vi.mock("@/core/api/generated", async (importOriginal) => {
	const actual = await importOriginal<typeof import("@/core/api/generated")>();
	return { ...actual, setNodeChatConversationSamplingOptions: setSamplingMock };
});

// The adapter imports the connection singleton at module load; this path never touches it.
vi.mock("@/features/chat/api/NodeChatConnection", () => ({ nodeChatConnection: {} }));

import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";

function conversationResponse(samplingOptions: unknown) {
	return { data: { conversationId: "conversation-1", messages: [], samplingOptions } };
}

describe("nodeChatAdapter.setConversationSamplingOptions", () => {
	beforeEach(() => {
		setSamplingMock.mockReset();
	});

	it("sends the wire settings and maps the stored ones back to the dialog's shape", async () => {
		setSamplingMock.mockResolvedValueOnce(
			conversationResponse({ temperature: 0.4, topP: null, seed: "42", stop: ["</end>"], numCtx: null }),
		);

		const conversation = await nodeChatAdapter.setConversationSamplingOptions("conversation-1", {
			temperature: 0.4,
			seed: "42",
			stop: ["</end>"],
		});

		expect(setSamplingMock.mock.calls[0]?.[0]).toMatchObject({
			path: { conversationId: "conversation-1" },
			body: { samplingOptions: { temperature: 0.4, seed: "42", stop: ["</end>"] } },
		});
		expect(conversation.samplingOptions).toEqual({ temperature: 0.4, seed: 42, stop: ["</end>"] });
	});

	it("sends null to clear, and a conversation without settings maps to none", async () => {
		setSamplingMock.mockResolvedValueOnce(conversationResponse(null));

		const conversation = await nodeChatAdapter.setConversationSamplingOptions("conversation-1", undefined);

		expect(setSamplingMock.mock.calls[0]?.[0]).toMatchObject({ body: { samplingOptions: null } });
		expect(conversation.samplingOptions).toBeUndefined();
	});

	it("keeps an empty stored object as settings of their own (model defaults), not as this browser's defaults", async () => {
		setSamplingMock.mockResolvedValueOnce(conversationResponse({}));

		const conversation = await nodeChatAdapter.setConversationSamplingOptions("conversation-1", {});

		expect(conversation.samplingOptions).toEqual({});
	});
});
