// @vitest-environment jsdom

import { renderHook, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, describe, expect, it, vi } from "vitest";

// The upload goes over the wire through MSW; only the toast surface is replaced so its message can be asserted.
const { toastErrorMock } = vi.hoisted(() => ({ toastErrorMock: vi.fn() }));

vi.mock("@/core/ui/notifications/Toast", () => ({
	toast: { error: toastErrorMock, success: vi.fn(), info: vi.fn(), warn: vi.fn(), warning: vi.fn(), progress: vi.fn() },
}));

import { useKnowledgeUpload } from "@/features/knowledge/queries/useKnowledgeUpload";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { createProvidersWrapper } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

function uploadOne(): void {
	const { wrapper } = createProvidersWrapper();
	const { result } = renderHook(() => useKnowledgeUpload(), { wrapper });
	result.current.uploadFiles([new File(["hello"], "notes.txt", { type: "text/plain" })]);
}

describe("useKnowledgeUpload", () => {
	afterEach(() => {
		vi.clearAllMocks();
	});

	it("names the file when the host refuses the body with a bare 413", async () => {
		server.use(http.post(localApiPath("knowledge-base/documents"), () => new HttpResponse(null, { status: 413 })));

		uploadOne();

		await waitFor(() => expect(toastErrorMock).toHaveBeenCalledWith("notes.txt is larger than this node accepts."));
	});

	it("shows the endpoint's own size-limit message from the validation errors map", async () => {
		const message = "The file exceeds the maximum upload size of 50 MB.";
		server.use(
			http.post(localApiPath("knowledge-base/documents"), () =>
				HttpResponse.json(
					{ statusCode: 400, message: "One or more errors occurred!", errors: { generalErrors: [message] } },
					{ status: 400 },
				),
			),
		);

		uploadOne();

		await waitFor(() => expect(toastErrorMock).toHaveBeenCalledWith(message));
	});
});
