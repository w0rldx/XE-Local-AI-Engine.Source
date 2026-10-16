// @vitest-environment jsdom

import { act, renderHook, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ZodError } from "zod";

// Mock the generated SDK (list/upload/delete) and the toast surface so error/validation paths can be asserted. No
// network.
const { listMock, postMock, deleteMock, toastErrorMock } = vi.hoisted(() => ({
	listMock: vi.fn(),
	postMock: vi.fn(),
	deleteMock: vi.fn(),
	toastErrorMock: vi.fn(),
}));

vi.mock("@/core/api/generated", () => ({
	listConversationFiles: listMock,
	uploadConversationFile: postMock,
	deleteConversationFile: deleteMock,
}));

vi.mock("@/core/ui/notifications/Toast", () => ({
	toast: { error: toastErrorMock, success: vi.fn(), info: vi.fn(), warn: vi.fn(), warning: vi.fn(), progress: vi.fn() },
}));

import { useConversationAttachments } from "@/features/chat/queries/useConversationAttachments";
import en from "@/locales/en.json";
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
		// The generated op gets the File itself (it serializes the multipart body) for the resolved conversation.
		const [options] = postMock.mock.calls[0] as [{ path: { conversationId: string }; body: { file: File } }];
		expect(options.path.conversationId).toBe("conversation-1");
		expect(options.body.file).toBe(file);
	});

	it("reports upload progress through the SDK call's onUploadProgress until the upload settles", async () => {
		let finishUpload: (value: { data: { fileId: string } }) => void = () => undefined;
		postMock.mockReturnValue(
			new Promise((resolve) => {
				finishUpload = resolve;
			}),
		);
		const { wrapper } = makeWrapper();
		const { result } = renderHook(() => useConversationAttachments({ conversationId: "conversation-1", ensureConversationId }), {
			wrapper,
		});

		result.current.uploadFiles([new File(["hello"], "notes.txt", { type: "text/plain" })]);
		await waitFor(() => expect(postMock).toHaveBeenCalledTimes(1));
		const [options] = postMock.mock.calls[0] as [{ onUploadProgress: (event: { loaded: number; total?: number }) => void }];

		act(() => options.onUploadProgress({ loaded: 50, total: 100 }));
		await waitFor(() => expect(result.current.pendingUploads[0]?.percent).toBe(50));

		finishUpload({ data: { fileId: "file-3" } });
		await waitFor(() => expect(result.current.pendingUploads).toHaveLength(0));
	});

	// A 200 whose body breaks the contract rejects with a ZodError; its issue list must never reach the toast.
	it("toasts the generic contract-mismatch sentence when the upload's success response is malformed", async () => {
		postMock.mockRejectedValue(
			new ZodError([{ code: "invalid_type", expected: "string", path: ["fileId"], message: "Invalid input" }]),
		);
		const { wrapper } = makeWrapper();
		const { result } = renderHook(() => useConversationAttachments({ conversationId: "conversation-1", ensureConversationId }), {
			wrapper,
		});

		result.current.uploadFiles([new File(["hello"], "notes.txt", { type: "text/plain" })]);

		await waitFor(() => expect(toastErrorMock).toHaveBeenCalledTimes(1));
		expect(toastErrorMock).toHaveBeenCalledWith(en.errorMessages.unexpectedResponseShape);
	});

	it("rejects a file over the node's reported limit client-side with a toast and never calls the upload endpoint", async () => {
		const { wrapper } = makeWrapper();
		const { result } = renderHook(
			() => useConversationAttachments({ conversationId: "conversation-1", ensureConversationId, maxUploadFileSizeMb: 1 }),
			{ wrapper },
		);

		// 2 MB > the 1 MB limit the conversation list reported.
		const oversize = new File([new Uint8Array(2 * 1024 * 1024)], "big.pdf", { type: "application/pdf" });
		result.current.uploadFiles([oversize]);

		await waitFor(() => expect(toastErrorMock).toHaveBeenCalledWith("big.pdf exceeds the 1 MB upload limit."));
		expect(postMock).not.toHaveBeenCalled();
		expect(ensureConversationId).not.toHaveBeenCalled();
	});

	it("leaves the size decision to the server while the node's limit is unknown", async () => {
		const { wrapper } = makeWrapper();
		const { result } = renderHook(() => useConversationAttachments({ conversationId: "conversation-1", ensureConversationId }), {
			wrapper,
		});

		// Larger than the old hard-coded 25 MB guess: without a reported limit there is no pre-check at all.
		const large = new File([new Uint8Array(26 * 1024 * 1024)], "big.pdf", { type: "application/pdf" });
		result.current.uploadFiles([large]);

		await waitFor(() => expect(postMock).toHaveBeenCalledTimes(1));
		expect(toastErrorMock).not.toHaveBeenCalled();
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
