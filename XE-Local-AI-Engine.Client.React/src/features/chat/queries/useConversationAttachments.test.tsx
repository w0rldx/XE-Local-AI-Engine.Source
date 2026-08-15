// @vitest-environment jsdom

import { renderHook, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Mock the generated SDK (list/delete), the axios instance (multipart upload posts through it directly), and the
// toast surface so error/validation paths can be asserted. No network.
const { listMock, postMock, deleteMock, toastErrorMock } = vi.hoisted(() => ({
	listMock: vi.fn(),
	postMock: vi.fn(),
	deleteMock: vi.fn(),
	toastErrorMock: vi.fn(),
}));

vi.mock("@/core/api/generated", () => ({
	listConversationFiles: listMock,
	deleteConversationFile: deleteMock,
}));

vi.mock("@/core/api/axios/AxiosInstance", () => ({
	axiosInstance: { post: postMock },
}));

vi.mock("@/core/ui/notifications/Toast", () => ({
	toast: { error: toastErrorMock, success: vi.fn(), info: vi.fn(), warn: vi.fn(), warning: vi.fn(), progress: vi.fn() },
}));

import { useConversationAttachments } from "@/features/chat/queries/useConversationAttachments";
import { createProvidersWrapper } from "@/test/RenderWithProviders";

function makeWrapper() {
	const { wrapper, queryClient } = createProvidersWrapper();
	return { wrapper, queryClient };
}

const ensureConversationId = vi.fn<() => Promise<string>>().mockResolvedValue("conversation-1");

describe("useConversationAttachments", () => {
	beforeEach(() => {
		listMock.mockResolvedValue({
			data: {
				items: [
					{ fileId: "file-1", originalFileName: "a.pdf", extension: ".pdf", sizeBytes: 10, extractionStatus: "Extracted" },
					{ fileId: "file-2", originalFileName: "b.txt", extension: ".txt", sizeBytes: 20, extractionStatus: "Pending" },
				],
			},
		});
		postMock.mockResolvedValue({ data: { fileId: "file-3" } });
		deleteMock.mockResolvedValue(undefined);
		ensureConversationId.mockResolvedValue("conversation-1");
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("loads the conversation's files and derives the attachment file ids", async () => {
		const { wrapper } = makeWrapper();
		const { result } = renderHook(() => useConversationAttachments({ conversationId: "conversation-1", ensureConversationId }), {
			wrapper,
		});

		await waitFor(() => expect(result.current.attachments).toHaveLength(2));

		expect(listMock).toHaveBeenCalledWith(expect.objectContaining({ path: { conversationId: "conversation-1" } }));
		expect(result.current.attachmentFileIds).toEqual(["file-1", "file-2"]);
		expect(result.current.attachments[0]?.status).toBe("extracted");
	});

	it("does not load files when there is no conversation yet", () => {
		const { wrapper } = makeWrapper();
		const { result } = renderHook(() => useConversationAttachments({ conversationId: "", ensureConversationId }), {
			wrapper,
		});

		expect(listMock).not.toHaveBeenCalled();
		expect(result.current.attachments).toHaveLength(0);
	});

	it("uploads accepted files as multipart form data into the resolved conversation", async () => {
		const { wrapper } = makeWrapper();
		const { result } = renderHook(() => useConversationAttachments({ conversationId: "conversation-1", ensureConversationId }), {
			wrapper,
		});

		const file = new File(["hello"], "notes.txt", { type: "text/plain" });
		result.current.uploadFiles([file]);

		await waitFor(() => expect(postMock).toHaveBeenCalledTimes(1));

		expect(ensureConversationId).toHaveBeenCalledTimes(1);
		// The upload posts a real FormData (field "file") through the shared axios instance with a multipart
		// content type, targeting the conversation's uploads endpoint.
		const [url, body, config] = postMock.mock.calls[0] as [string, FormData, { headers: Record<string, string> }];
		expect(url).toContain("chat/conversations/conversation-1/uploads");
		expect(body).toBeInstanceOf(FormData);
		expect((body.get("file") as File).name).toBe("notes.txt");
		expect(config.headers["Content-Type"]).toBe("multipart/form-data");
	});

	it("rejects an oversize file client-side with a toast and never calls the upload endpoint", async () => {
		const { wrapper } = makeWrapper();
		const { result } = renderHook(() => useConversationAttachments({ conversationId: "conversation-1", ensureConversationId }), {
			wrapper,
		});

		// 26 MB > the 25 MB advisory cap.
		const oversize = new File([new Uint8Array(26 * 1024 * 1024)], "big.pdf", { type: "application/pdf" });
		result.current.uploadFiles([oversize]);

		await waitFor(() => expect(toastErrorMock).toHaveBeenCalledTimes(1));
		expect(postMock).not.toHaveBeenCalled();
		expect(ensureConversationId).not.toHaveBeenCalled();
	});

	it("removes an attachment via the delete endpoint", async () => {
		const { wrapper } = makeWrapper();
		const { result } = renderHook(() => useConversationAttachments({ conversationId: "conversation-1", ensureConversationId }), {
			wrapper,
		});

		result.current.removeAttachment("file-1");

		await waitFor(() => expect(deleteMock).toHaveBeenCalledTimes(1));
		expect(deleteMock).toHaveBeenCalledWith(
			expect.objectContaining({ path: { conversationId: "conversation-1", fileId: "file-1" } }),
		);
	});
});
