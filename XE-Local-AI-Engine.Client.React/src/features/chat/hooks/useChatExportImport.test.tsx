// @vitest-environment jsdom

import { act, renderHook, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

// The generated SDK, the download and the toast surface are mocked: these tests pin what the hook asks for, what it
// saves and what it tells the operator. No network.
const { exportMock, importMock, saveBlobMock, toastErrorMock, toastSuccessMock, toastProgressMock } = vi.hoisted(() => ({
	exportMock: vi.fn(),
	importMock: vi.fn(),
	saveBlobMock: vi.fn(),
	toastErrorMock: vi.fn(),
	toastSuccessMock: vi.fn(),
	toastProgressMock: vi.fn(),
}));

vi.mock("@/core/api/generated", () => ({
	exportNodeChatConversations: exportMock,
	importNodeChatConversations: importMock,
}));

const { hub } = vi.hoisted(() => ({ hub: { on: vi.fn(), off: vi.fn() } }));

vi.mock("@/features/chat/api/NodeChatConnection", () => ({
	nodeChatConnection: { ensureConnection: vi.fn(async () => hub) },
}));

vi.mock("@/core/api/utils/DownloadBlob", () => ({ saveBlob: saveBlobMock }));

vi.mock("@/core/ui/notifications/Toast", () => ({
	toast: {
		error: toastErrorMock,
		success: toastSuccessMock,
		info: vi.fn(),
		warn: vi.fn(),
		warning: vi.fn(),
		progress: toastProgressMock,
	},
}));

import { nodeChatConnection } from "@/features/chat/api/NodeChatConnection";
import { CHAT_IMPORT_PROGRESS, useChatExportImport } from "@/features/chat/hooks/useChatExportImport";
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
			expect(toastSuccessMock).toHaveBeenCalledWith(en.chat.exportImport.imported_one.replace("{{count}}", "1"), {
				id: "chat-import",
			}),
		);
		expect(importMock).toHaveBeenCalledWith({ body: exportDocument, query: { importId: expect.any(String) } });
	});

	it("shows the node's progress for its own import id only, and stops listening once the node answers", async () => {
		let answer: (value: unknown) => void = () => undefined;
		importMock.mockReturnValue(
			new Promise((resolve) => {
				answer = resolve;
			}),
		);
		const result = renderExportImport();

		act(() =>
			result.current.importFile(new File([JSON.stringify(exportDocument)], "chats.chat.json", { type: "application/json" })),
		);

		await waitFor(() => expect(importMock).toHaveBeenCalled());
		const [request] = importMock.mock.calls[0] as [{ query: { importId: string } }];
		const importId = request.query.importId;
		await waitFor(() => expect(hub.on).toHaveBeenCalledWith(CHAT_IMPORT_PROGRESS, expect.any(Function)));
		const push = hub.on.mock.calls[0]?.[1] as (event: unknown) => void;
		toastProgressMock.mockClear();

		push({ importId: "another-import", importedChats: 5, totalChats: 10, state: "running" });
		push({ importId, importedChats: 300, totalChats: 1500, state: "running" });
		expect(toastProgressMock).toHaveBeenCalledTimes(1);
		expect(toastProgressMock).toHaveBeenCalledWith({
			id: "chat-import",
			message: en.chat.exportImport.importProgress.replace("{{done}}", "300").replace("{{total}}", "1500"),
			percent: 20,
		});

		await act(async () =>
			answer({ data: { conversations: [{ conversationId: "new-1", title: "Trip plan", messageCount: 1 }] } }),
		);
		await waitFor(() => expect(toastSuccessMock).toHaveBeenCalledWith(expect.any(String), { id: "chat-import" }));
		expect(hub.off).toHaveBeenCalledWith(CHAT_IMPORT_PROGRESS, push);
	});

	it("sends the import without waiting for the chat hub, and never listens once the node has answered", async () => {
		// A hub that is down reconnects for many seconds; the import must not wait on it.
		let connect: (value: typeof hub) => void = () => undefined;
		vi.mocked(nodeChatConnection.ensureConnection).mockReturnValueOnce(
			new Promise((resolve) => {
				connect = resolve;
			}) as never,
		);
		importMock.mockResolvedValue({ data: { conversations: [{ conversationId: "new-1", title: "Trip plan", messageCount: 1 }] } });
		const result = renderExportImport();

		act(() =>
			result.current.importFile(new File([JSON.stringify(exportDocument)], "chats.chat.json", { type: "application/json" })),
		);

		await waitFor(() => expect(toastSuccessMock).toHaveBeenCalledWith(expect.any(String), { id: "chat-import" }));
		await act(async () => connect(hub));
		expect(hub.on).not.toHaveBeenCalled();
	});

	it("stays busy with a sticky notice until the node answers, then replaces the notice with the result", async () => {
		let answer: (value: unknown) => void = () => undefined;
		importMock.mockReturnValue(
			new Promise((resolve) => {
				answer = resolve;
			}),
		);
		const result = renderExportImport();
		expect(result.current.importing).toBe(false);

		act(() =>
			result.current.importFile(new File([JSON.stringify(exportDocument)], "chats.chat.json", { type: "application/json" })),
		);

		await waitFor(() => expect(importMock).toHaveBeenCalled());
		expect(result.current.importing).toBe(true);
		expect(toastProgressMock).toHaveBeenCalledWith({
			id: "chat-import",
			message: en.chat.exportImport.importing_one.replace("{{count}}", "1"),
		});
		expect(toastSuccessMock).not.toHaveBeenCalled();

		await act(async () =>
			answer({ data: { conversations: [{ conversationId: "new-1", title: "Trip plan", messageCount: 1 }] } }),
		);

		await waitFor(() => expect(result.current.importing).toBe(false));
		expect(toastSuccessMock).toHaveBeenCalledWith(expect.any(String), { id: "chat-import" });
	});

	it("shows the server's refusal when the import is rejected", async () => {
		importMock.mockRejectedValue(new Error("Message role 'tool' cannot be imported"));
		const result = renderExportImport();

		act(() =>
			result.current.importFile(new File([JSON.stringify(exportDocument)], "chats.chat.json", { type: "application/json" })),
		);

		await waitFor(() => expect(toastErrorMock).toHaveBeenCalledWith(expect.any(String), { id: "chat-import" }));
		expect(toastSuccessMock).not.toHaveBeenCalled();
		await waitFor(() => expect(result.current.importing).toBe(false));
	});
});
