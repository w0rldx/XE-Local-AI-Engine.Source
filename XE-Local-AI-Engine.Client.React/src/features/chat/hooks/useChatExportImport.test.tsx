// @vitest-environment jsdom

import { renderHook, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

// The generated SDK, the download and the toast surface are mocked: these tests pin what the hook asks for, what it
// saves and what it tells the operator. No network.
const { exportMock, importMock, saveBlobMock, toastErrorMock, toastSuccessMock } = vi.hoisted(() => ({
	exportMock: vi.fn(),
	importMock: vi.fn(),
	saveBlobMock: vi.fn(),
	toastErrorMock: vi.fn(),
	toastSuccessMock: vi.fn(),
}));

vi.mock("@/core/api/generated", () => ({
	exportNodeChatConversations: exportMock,
	importNodeChatConversations: importMock,
}));

vi.mock("@/core/api/utils/DownloadBlob", () => ({ saveBlob: saveBlobMock }));

vi.mock("@/core/ui/notifications/Toast", () => ({
	toast: { error: toastErrorMock, success: toastSuccessMock, info: vi.fn(), warn: vi.fn(), warning: vi.fn(), progress: vi.fn() },
}));

import { useChatExportImport } from "@/features/chat/hooks/useChatExportImport";
import { CHAT_IMPORT_READ_LIMIT_BYTES } from "@/features/chat/models/ChatExportFile";
import en from "@/locales/en.json";
import { createProvidersWrapper } from "@/test/RenderWithProviders";

const exportDocument = {
	format: "xe-chat-export",
	formatVersion: 1,
	conversations: [
		{
			title: "Trip plan",
			createdAtUtc: 1,
			messages: [{ role: "user", content: "Where to?", createdAtUtc: 2 }],
		},
	],
};

function renderExportImport() {
	const { wrapper } = createProvidersWrapper();
	return renderHook(() => useChatExportImport(), { wrapper }).result;
}

/** The one download the hook started; the caller has already waited for `saveBlob` to be called. */
function savedBlob(): [Blob, string] {
	return saveBlobMock.mock.calls[0] as [Blob, string];
}

describe("useChatExportImport", () => {
	afterEach(() => {
		vi.clearAllMocks();
	});

	it("downloads one chat as Markdown under its title", async () => {
		exportMock.mockResolvedValue({ data: exportDocument });
		const result = renderExportImport();

		result.current.exportConversation("conversation-1", "markdown");

		await waitFor(() => expect(saveBlobMock).toHaveBeenCalled());
		const [blob, fileName] = savedBlob();
		expect(exportMock).toHaveBeenCalledWith({ query: { conversationId: "conversation-1" } });
		expect(fileName).toBe("trip-plan.md");
		expect(blob.type).toBe("text/markdown");
		expect(await blob.text()).toContain("Where to?");
	});

	it("downloads one chat as the JSON export file", async () => {
		exportMock.mockResolvedValue({ data: exportDocument });
		const result = renderExportImport();

		result.current.exportConversation("conversation-1", "json");

		await waitFor(() => expect(saveBlobMock).toHaveBeenCalled());
		const [blob, fileName] = savedBlob();
		expect(fileName).toBe("trip-plan.chat.json");
		expect(JSON.parse(await blob.text())).toEqual(exportDocument);
	});

	it("downloads every chat with no conversation filter", async () => {
		exportMock.mockResolvedValue({ data: exportDocument });
		const result = renderExportImport();

		result.current.exportAll();

		await waitFor(() => expect(saveBlobMock).toHaveBeenCalled());
		expect(exportMock).toHaveBeenCalledWith({ query: {} });
		expect(savedBlob()[1]).toBe("chats.chat.json");
	});

	it("says so when an export fails", async () => {
		exportMock.mockRejectedValue(new Error("offline"));
		const result = renderExportImport();

		result.current.exportAll();

		await waitFor(() => expect(toastErrorMock).toHaveBeenCalled());
		expect(saveBlobMock).not.toHaveBeenCalled();
	});

	it.each([
		["not json", en.chat.exportImport.notJson],
		[JSON.stringify({ format: "something-else", formatVersion: 1, conversations: [] }), en.chat.exportImport.wrongFormat],
		[JSON.stringify({ format: "xe-chat-export", formatVersion: 9, conversations: [] }), en.chat.exportImport.unsupportedVersion],
	])("refuses %s before posting anything", async (text, message) => {
		const result = renderExportImport();

		result.current.importFile(new File([text], "chats.chat.json", { type: "application/json" }));

		await waitFor(() => expect(toastErrorMock).toHaveBeenCalledWith(message));
		expect(importMock).not.toHaveBeenCalled();
	});

	it("refuses a file past the read limit without reading it", () => {
		const file = new File(["{}"], "huge.chat.json", { type: "application/json" });
		Object.defineProperty(file, "size", { value: CHAT_IMPORT_READ_LIMIT_BYTES + 1 });
		const text = vi.spyOn(file, "text");
		const result = renderExportImport();

		result.current.importFile(file);

		expect(toastErrorMock).toHaveBeenCalledWith(en.chat.exportImport.tooLarge);
		expect(text).not.toHaveBeenCalled();
		expect(importMock).not.toHaveBeenCalled();
	});

	it("posts a chat export and reports how many chats it imported", async () => {
		importMock.mockResolvedValue({ data: { conversations: [{ conversationId: "new-1", title: "Trip plan", messageCount: 1 }] } });
		const result = renderExportImport();

		result.current.importFile(new File([JSON.stringify(exportDocument)], "chats.chat.json", { type: "application/json" }));

		await waitFor(() =>
			expect(toastSuccessMock).toHaveBeenCalledWith(en.chat.exportImport.imported_one.replace("{{count}}", "1")),
		);
		expect(importMock).toHaveBeenCalledWith({ body: exportDocument });
	});

	it("shows the server's refusal when the import is rejected", async () => {
		importMock.mockRejectedValue(new Error("Message role 'tool' cannot be imported"));
		const result = renderExportImport();

		result.current.importFile(new File([JSON.stringify(exportDocument)], "chats.chat.json", { type: "application/json" }));

		await waitFor(() => expect(toastErrorMock).toHaveBeenCalled());
		expect(toastSuccessMock).not.toHaveBeenCalled();
	});
});
