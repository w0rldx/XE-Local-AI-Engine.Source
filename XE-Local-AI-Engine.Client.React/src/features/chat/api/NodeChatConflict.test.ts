import { AxiosError } from "axios";
import { describe, expect, it } from "vitest";

import { ApiError } from "@/core/api/errors/ApiError";
import type { ConflictProblemDetails } from "@/core/api/models/ProblemDetails";
import { isNodeChatReadOnlyConflict } from "@/features/chat/api/NodeChatConflict";

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
});
