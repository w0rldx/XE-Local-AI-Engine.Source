import { t } from "i18next";
import { describe, expect, it } from "vitest";

import { ApiError } from "@/core/api/errors/ApiError";
import type { AgentOption } from "@/core/agents/models/AgentOption";
import type { ProblemDetails } from "@/core/api/models/ProblemDetails";
import type { ModelOption } from "@/core/models/ModelOption";
import { localDefaultModelValue } from "@/core/models/NodeChatModelSelection";
import { clientWatchdogFailureCategory, StreamWatchdogError } from "@/features/chat/api/NodeChatStreamGuard";
import type { ChatConversationModel } from "@/features/chat/models/ChatModels";
import {
	effectiveAgent,
	needsFirstSendTitle,
	resolveRequestModel,
	sendFailureMessage,
} from "@/features/chat/models/ChatTurnResolution";

function modelOption(value: string): ModelOption {
	return { value, label: value } as ModelOption;
}

function agentOption(id: string): AgentOption {
	return { id, name: `Agent ${id}`, description: "", kind: "Single", modelProfile: null, playbookEnabled: false };
}

function conversation(overrides: Partial<ChatConversationModel> = {}): ChatConversationModel {
	return {
		id: "conversation-1",
		title: "New conversation",
		origin: "local",
		createdAt: "2026-05-24T00:00:00.000Z",
		updatedAt: "2026-05-24T00:00:00.000Z",
		messages: [],
		...overrides,
	};
}

function conflict(conflictType: string): ApiError {
	return new ApiError(409, { title: "Conflict", status: 409, conflictType } as unknown as ProblemDetails);
}

describe("resolveRequestModel", () => {
	it("keeps a model that is still a local or cloud option", () => {
		expect(resolveRequestModel("local-a", [modelOption("local-a")], [])).toBe("local-a");
		expect(resolveRequestModel("cloud-a", [], [modelOption("cloud-a")])).toBe("cloud-a");
	});

	it("drops a stale selection so the node resolves its default", () => {
		expect(resolveRequestModel("gone", [modelOption("local-a")], [modelOption("cloud-a")])).toBeUndefined();
	});

	it("sends no model for the local-default sentinel even when an option carries that value", () => {
		expect(resolveRequestModel(localDefaultModelValue, [modelOption(localDefaultModelValue)], [])).toBeUndefined();
	});
});

describe("effectiveAgent", () => {
	const options = [agentOption("agent-1")];

	it("resolves a selected agent that is still listed while agent mode is on", () => {
		expect(effectiveAgent(true, "agent-1", options)?.id).toBe("agent-1");
	});

	it("resolves nothing with agent mode off, no selection, or a stale id", () => {
		expect(effectiveAgent(false, "agent-1", options)).toBeUndefined();
		expect(effectiveAgent(true, "", options)).toBeUndefined();
		expect(effectiveAgent(true, "deleted", options)).toBeUndefined();
	});
});

describe("needsFirstSendTitle", () => {
	it("is true for a placeholder or blank title with no user message", () => {
		expect(needsFirstSendTitle(conversation())).toBe(true);
		expect(needsFirstSendTitle(conversation({ title: "   " }))).toBe(true);
	});

	it("is false once named, for a remote conversation, or after a user message", () => {
		expect(needsFirstSendTitle(conversation({ title: "Named" }))).toBe(false);
		expect(needsFirstSendTitle(conversation({ origin: "remote" }))).toBe(false);
		expect(
			needsFirstSendTitle(
				conversation({
					messages: [
						{
							id: "user-1",
							conversationId: "conversation-1",
							role: "user",
							content: "hello",
							status: "completed",
							createdAt: "2026-05-24T00:00:00.000Z",
							sortOrder: 0,
						},
					],
				}),
			),
		).toBe(false);
	});
});

describe("sendFailureMessage", () => {
	it("maps each refusal to its own sentence, with no failure category", () => {
		expect(sendFailureMessage(conflict("ReadOnlyConversation"), t)).toEqual({
			message: "This conversation was started from a paired client and is view-only on this node.",
			failureCategory: undefined,
		});
		expect(sendFailureMessage(conflict("GraphWorkflowRunLiveInConversation"), t)).toEqual({
			message: "This conversation is running a workflow; answer through it or stop it first.",
			failureCategory: undefined,
		});
		expect(sendFailureMessage(conflict("ConversationTurnParked"), t)).toEqual({
			message: "A reply in this conversation is waiting for your answer. Answer the waiting card, then send again.",
			failureCategory: undefined,
		});
	});

	it("names the browser's watchdog in words and tags its reason code", () => {
		expect(
			sendFailureMessage(new StreamWatchdogError("no-first-chunk", "Local chat stream timed out (no-first-chunk)."), t),
		).toEqual({
			message: "Connection watchdog: the response never started arriving in this browser.",
			failureCategory: clientWatchdogFailureCategory,
		});
	});

	it("keeps any other error's own text", () => {
		expect(sendFailureMessage(new Error("Node-side bound fired."), t)).toEqual({
			message: "Node-side bound fired.",
			failureCategory: undefined,
		});
	});
});
