import { AxiosError } from "axios";
import { describe, expect, it } from "vitest";

import { ApiError } from "@/core/api/errors/ApiError";
import type { ConflictProblemDetails } from "@/core/api/models/ProblemDetails";
import {
	isNodeChatReadOnlyConflict,
	isNodeChatWorkflowRunLiveConflict,
	stripSignalRHubErrorPrefix,
} from "@/features/chat/api/NodeChatConflict";

function conflict(conflictType: string): ConflictProblemDetails {
	return {
		type: "https://tools.ietf.org/html/rfc7231#section-6.5.8",
		title: "Conflict",
		status: 409,
		detail: "Conversation 0e5b is read-only because it has remote origin.",
		conflictType,
	};
}

describe("isNodeChatReadOnlyConflict", () => {
	it("matches a 409 ApiError carrying the ReadOnlyConversation conflictType", () => {
		expect(isNodeChatReadOnlyConflict(new ApiError(409, conflict("ReadOnlyConversation")))).toBe(true);
	});

	it("rejects a 409 ApiError carrying a different conflictType", () => {
		expect(isNodeChatReadOnlyConflict(new ApiError(409, conflict("WorkspaceRevocationBusy")))).toBe(false);
	});

	it("rejects a non-409 ApiError even with the read-only conflictType", () => {
		expect(isNodeChatReadOnlyConflict(new ApiError(400, conflict("ReadOnlyConversation")))).toBe(false);
	});

	it("rejects errors that are not ApiError", () => {
		// The interceptor rethrows every non-2xx as ApiError, so a raw AxiosError never reaches a caller: the old
		// isAxiosError-based check matched nothing. Pin that this helper does not regress to it.
		const axiosError = new AxiosError("conflict", undefined, undefined, undefined, {
			status: 409,
			statusText: "Conflict",
			headers: {},
			// biome-ignore lint/suspicious/noExplicitAny: minimal axios response stub for the negative case.
			config: {} as any,
			data: conflict("ReadOnlyConversation"),
		});

		expect(isNodeChatReadOnlyConflict(axiosError)).toBe(false);
		expect(isNodeChatReadOnlyConflict(new Error("boom"))).toBe(false);
		expect(isNodeChatReadOnlyConflict(undefined)).toBe(false);
	});

	// The chat hub has no 409 to hand back: SignalR forwards a HubException's message and nothing else, so the
	// backend leads that message with the same token the REST 409 carries as conflictType.
	it("matches a SignalR HubException wrapped in SignalR's generic invocation prefix", () => {
		const wrapped = new Error(
			"An unexpected error occurred invoking 'SendMessage' on the server. HubException: ReadOnlyConversation: Conversation 0e5b is read-only because it has remote origin.",
		);

		expect(isNodeChatReadOnlyConflict(wrapped)).toBe(true);
	});

	it("matches an unwrapped HubException message", () => {
		expect(
			isNodeChatReadOnlyConflict(new Error("ReadOnlyConversation: Conversation 0e5b is read-only because it has remote origin.")),
		).toBe(true);
	});

	it("rejects a SignalR HubException carrying any other message", () => {
		const sizeCap = new Error(
			"An unexpected error occurred invoking 'SendMessage' on the server. HubException: Your message is too large (513 KB, limit 512 KB).",
		);

		expect(isNodeChatReadOnlyConflict(sizeCap)).toBe(false);
	});
});

describe("isNodeChatWorkflowRunLiveConflict", () => {
	it("matches the REST 409 and the wrapped hub refusal, and nothing else", () => {
		expect(isNodeChatWorkflowRunLiveConflict(new ApiError(409, conflict("GraphWorkflowRunLiveInConversation")))).toBe(true);
		expect(
			isNodeChatWorkflowRunLiveConflict(
				new Error(
					"An unexpected error occurred invoking 'SendMessage' on the server. HubException: GraphWorkflowRunLiveInConversation: A workflow run is live.",
				),
			),
		).toBe(true);
		// The hub's stream refuses once enumerated, so SignalR wraps it in its streaming-results sentence instead.
		expect(
			isNodeChatWorkflowRunLiveConflict(
				new Error(
					"An error occurred on the server while streaming results. HubException: GraphWorkflowRunLiveInConversation: Conversation 0e5b has a workflow run in progress; answer or stop the workflow before sending a normal message.",
				),
			),
		).toBe(true);
		expect(isNodeChatWorkflowRunLiveConflict(new ApiError(409, conflict("ReadOnlyConversation")))).toBe(false);
		expect(isNodeChatWorkflowRunLiveConflict(new Error("ReadOnlyConversation: read-only."))).toBe(false);
		expect(isNodeChatReadOnlyConflict(new Error("GraphWorkflowRunLiveInConversation: live."))).toBe(false);
	});
});

describe("stripSignalRHubErrorPrefix", () => {
	it("removes SignalR's wrapper and leaves anything else untouched", () => {
		expect(
			stripSignalRHubErrorPrefix("An unexpected error occurred invoking 'RegenerateMessage' on the server. HubException: nope"),
		).toBe("nope");
		expect(
			stripSignalRHubErrorPrefix(
				"An error occurred on the server while streaming results. HubException: ReadOnlyConversation: x",
			),
		).toBe("ReadOnlyConversation: x");
		expect(stripSignalRHubErrorPrefix("plain message")).toBe("plain message");
	});
});
