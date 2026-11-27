// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import i18next from "i18next";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ConversationList } from "@/features/chat/components/ConversationList";
import type { ChatConversationModel, ChatConversationSearchModel } from "@/features/chat/models/ChatModels";
import de from "@/locales/de.json";
import { installJsdomEnvironmentMocks, renderWithMantine, testMantineTheme } from "@/test/MantineTestRender";

function conversation(overrides: Partial<ChatConversationModel> = {}): ChatConversationModel {
	return {
		id: "conversation-1",
		title: "A conversation",
		createdAt: "2026-05-24T00:00:00.000Z",
		updatedAt: "2026-05-24T00:00:00.000Z",
		messages: [],
		...overrides,
	};
}

describe("ConversationList origin badge", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("renders a Remote badge for remote-origin conversations", () => {
		renderWithMantine(
			<ConversationList
				conversations={[conversation({ id: "remote-1", origin: "remote" })]}
				onCreateConversation={vi.fn()}
				onSelect={vi.fn()}
				onToggleCollapse={vi.fn()}
			/>,
		);

		expect(screen.getByTestId("conversation-remote-badge-remote-1")).toBeTruthy();
	});

	it("does not render a Remote badge for local conversations", () => {
		renderWithMantine(
			<ConversationList
				conversations={[conversation({ id: "local-1", origin: "local" })]}
				onCreateConversation={vi.fn()}
				onSelect={vi.fn()}
				onToggleCollapse={vi.fn()}
			/>,
		);

		expect(screen.queryByTestId("conversation-remote-badge-local-1")).toBeNull();
	});
});

describe("ConversationList management actions", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("hides the actions menu for remote-origin (view-only) conversations", () => {
		renderWithMantine(
			<ConversationList
				conversations={[conversation({ id: "remote-1", origin: "remote" })]}
				onCreateConversation={vi.fn()}
				onSelect={vi.fn()}
				onToggleCollapse={vi.fn()}
				onRename={vi.fn()}
				onTogglePin={vi.fn()}
				onToggleArchive={vi.fn()}
			/>,
		);

		expect(screen.queryByTestId("conversation-actions-remote-1")).toBeNull();
	});

	it("invokes onTogglePin with the negated pin state", async () => {
		const onTogglePin = vi.fn();
		renderWithMantine(
			<ConversationList
				conversations={[conversation({ id: "local-1", origin: "local", isPinned: false })]}
				onCreateConversation={vi.fn()}
				onSelect={vi.fn()}
				onToggleCollapse={vi.fn()}
				onTogglePin={onTogglePin}
			/>,
		);

		fireEvent.click(screen.getByTestId("conversation-actions-local-1"));
		fireEvent.click(await screen.findByTestId("conversation-pin-local-1"));

		expect(onTogglePin).toHaveBeenCalledWith("local-1", true);
	});

	it("invokes onToggleArchive with the negated archive state", async () => {
		const onToggleArchive = vi.fn();
		renderWithMantine(
			<ConversationList
				conversations={[conversation({ id: "local-1", origin: "local", isArchived: false })]}
				onCreateConversation={vi.fn()}
				onSelect={vi.fn()}
				onToggleCollapse={vi.fn()}
				onToggleArchive={onToggleArchive}
			/>,
		);

		fireEvent.click(screen.getByTestId("conversation-actions-local-1"));
		fireEvent.click(await screen.findByTestId("conversation-archive-local-1"));

		expect(onToggleArchive).toHaveBeenCalledWith("local-1", true);
	});

	it("invokes onDelete with skipConfirm=false for a plain click", async () => {
		const onDelete = vi.fn();
		renderWithMantine(
			<ConversationList
				conversations={[conversation({ id: "local-1", origin: "local" })]}
				onCreateConversation={vi.fn()}
				onSelect={vi.fn()}
				onToggleCollapse={vi.fn()}
				onDelete={onDelete}
			/>,
		);

		fireEvent.click(screen.getByTestId("conversation-actions-local-1"));
		fireEvent.click(await screen.findByTestId("conversation-delete-local-1"));

		expect(onDelete).toHaveBeenCalledWith("local-1", false);
	});

	it("invokes onDelete with skipConfirm=true when the delete item is Shift-clicked", async () => {
		const onDelete = vi.fn();
		renderWithMantine(
			<ConversationList
				conversations={[conversation({ id: "local-1", origin: "local" })]}
				onCreateConversation={vi.fn()}
				onSelect={vi.fn()}
				onToggleCollapse={vi.fn()}
				onDelete={onDelete}
			/>,
		);

		fireEvent.click(screen.getByTestId("conversation-actions-local-1"));
		fireEvent.click(await screen.findByTestId("conversation-delete-local-1"), { shiftKey: true });

		expect(onDelete).toHaveBeenCalledWith("local-1", true);
	});

	it("surfaces a Shift-click hint on the delete item without changing the skip behavior", async () => {
		const onDelete = vi.fn();
		renderWithMantine(
			<ConversationList
				conversations={[conversation({ id: "local-1", origin: "local" })]}
				onCreateConversation={vi.fn()}
				onSelect={vi.fn()}
				onToggleCollapse={vi.fn()}
				onDelete={onDelete}
			/>,
		);

		fireEvent.click(screen.getByTestId("conversation-actions-local-1"));
		const deleteItem = await screen.findByTestId("conversation-delete-local-1");

		// The hint becomes discoverable on hover and does not alter the existing skip-confirm behavior.
		fireEvent.mouseEnter(deleteItem);
		expect(await screen.findByText("Shift+Click to skip confirm")).toBeTruthy();
	});

	it("does not render the delete item for remote-origin (view-only) conversations", () => {
		renderWithMantine(
			<ConversationList
				conversations={[conversation({ id: "remote-1", origin: "remote" })]}
				onCreateConversation={vi.fn()}
				onSelect={vi.fn()}
				onToggleCollapse={vi.fn()}
				onDelete={vi.fn()}
			/>,
		);

		expect(screen.queryByTestId("conversation-delete-remote-1")).toBeNull();
	});

	it("commits a rename on Enter and does not re-select the conversation while editing", async () => {
		const onRename = vi.fn();
		const onSelect = vi.fn();
		renderWithMantine(
			<ConversationList
				conversations={[conversation({ id: "local-1", origin: "local", title: "Old title" })]}
				onCreateConversation={vi.fn()}
				onSelect={onSelect}
				onToggleCollapse={vi.fn()}
				onRename={onRename}
			/>,
		);

		fireEvent.click(screen.getByTestId("conversation-actions-local-1"));
		fireEvent.click(await screen.findByTestId("conversation-rename-local-1"));

		const input = (await screen.findByTestId("conversation-rename-input-local-1")) as HTMLInputElement;
		fireEvent.change(input, { target: { value: "New title" } });
		fireEvent.keyDown(input, { key: "Enter" });

		expect(onRename).toHaveBeenCalledWith("local-1", "New title");
		expect(onSelect).not.toHaveBeenCalled();
	});

	it("keeps the rename open on an Enter that confirms an IME composition and commits on a plain Enter", async () => {
		const onRename = vi.fn();
		renderWithMantine(
			<ConversationList
				conversations={[conversation({ id: "local-1", origin: "local", title: "Old title" })]}
				onCreateConversation={vi.fn()}
				onSelect={vi.fn()}
				onToggleCollapse={vi.fn()}
				onRename={onRename}
			/>,
		);

		fireEvent.click(screen.getByTestId("conversation-actions-local-1"));
		fireEvent.click(await screen.findByTestId("conversation-rename-local-1"));
		const input = (await screen.findByTestId("conversation-rename-input-local-1")) as HTMLInputElement;
		fireEvent.change(input, { target: { value: "新しい題名" } });

		fireEvent.keyDown(input, { key: "Enter", isComposing: true });
		fireEvent.keyDown(input, { key: "Enter", keyCode: 229 });
		expect(onRename).not.toHaveBeenCalled();
		expect(screen.getByTestId("conversation-rename-input-local-1")).toBe(input);

		fireEvent.keyDown(input, { key: "Enter" });
		expect(onRename).toHaveBeenCalledWith("local-1", "新しい題名");
	});

	it("filters conversations by the search query against title and preview", () => {
		renderWithMantine(
			<ConversationList
				conversations={[
					conversation({ id: "alpha", title: "Alpha planning" }),
					conversation({ id: "beta", title: "Beta notes", lastMessagePreview: "alpha appears here" }),
					conversation({ id: "gamma", title: "Gamma report" }),
				]}
				searchQuery="alpha"
				onCreateConversation={vi.fn()}
				onSelect={vi.fn()}
				onToggleCollapse={vi.fn()}
				onSearchChange={vi.fn()}
			/>,
		);

		expect(screen.getByTestId("conversation-item-alpha")).toBeTruthy();
		expect(screen.getByTestId("conversation-item-beta")).toBeTruthy();
		expect(screen.queryByTestId("conversation-item-gamma")).toBeNull();
	});

	it("shows the server's content matches, snippet included, in place of the local filter once they arrive", () => {
		renderWithMantine(
			<ConversationList
				conversations={[conversation({ id: "alpha", title: "Alpha planning" })]}
				searchQuery="granada"
				searchResults={{
					conversations: [
						conversation({ id: "older", title: "Trip notes", lastMessagePreview: "…visit the Alhambra in Granada…" }),
					],
					indexing: false,
				}}
				onCreateConversation={vi.fn()}
				onSelect={vi.fn()}
				onToggleCollapse={vi.fn()}
				onSearchChange={vi.fn()}
			/>,
		);

		// "older" is not among the loaded rows: only the server could have found it, by message text.
		expect(screen.getByTestId("conversation-item-older").textContent).toContain("Alhambra in Granada");
		expect(screen.queryByTestId("conversation-item-alpha")).toBeNull();
		expect(screen.queryByTestId("conversation-search-indexing")).toBeNull();
	});

	it("previews a title-only hit as a title match, not as an empty chat", () => {
		renderWithMantine(
			<ConversationList
				conversations={[]}
				searchQuery="tangerine"
				searchResults={{
					conversations: [conversation({ id: "titled", title: "Tangerine notes", lastMessagePreview: undefined })],
					indexing: false,
				}}
				onCreateConversation={vi.fn()}
				onSelect={vi.fn()}
				onToggleCollapse={vi.fn()}
				onSearchChange={vi.fn()}
			/>,
		);

		const row = screen.getByTestId("conversation-item-titled");
		expect(row.textContent).toContain("Matches the title");
		expect(row.textContent).not.toContain("No messages");
	});

	it("says results may be incomplete while the node is still indexing", () => {
		renderWithMantine(
			<ConversationList
				conversations={[]}
				searchQuery="granada"
				searchResults={{ conversations: [], indexing: true }}
				onCreateConversation={vi.fn()}
				onSelect={vi.fn()}
				onToggleCollapse={vi.fn()}
				onSearchChange={vi.fn()}
			/>,
		);

		expect(screen.getByTestId("conversation-search-indexing").textContent).toContain("Still indexing");
		expect(screen.getByTestId("conversation-list-empty").textContent).toContain("No conversations match");
	});

	it("ignores stale server results once the search box is cleared", () => {
		renderWithMantine(
			<ConversationList
				conversations={[conversation({ id: "alpha", title: "Alpha planning" })]}
				searchQuery=""
				searchResults={{ conversations: [conversation({ id: "older" })], indexing: true }}
				onCreateConversation={vi.fn()}
				onSelect={vi.fn()}
				onToggleCollapse={vi.fn()}
				onSearchChange={vi.fn()}
			/>,
		);

		expect(screen.getByTestId("conversation-item-alpha")).toBeTruthy();
		expect(screen.queryByTestId("conversation-item-older")).toBeNull();
		expect(screen.queryByTestId("conversation-search-indexing")).toBeNull();
	});

	it("hides archived conversations until show-archived is enabled", () => {
		const archivedConversation = conversation({ id: "archived-1", isArchived: true });
		const { rerender } = renderWithMantine(
			<ConversationList
				conversations={[archivedConversation]}
				showArchived={false}
				onCreateConversation={vi.fn()}
				onSelect={vi.fn()}
				onToggleCollapse={vi.fn()}
				onToggleShowArchived={vi.fn()}
			/>,
		);

		expect(screen.queryByTestId("conversation-item-archived-1")).toBeNull();

		rerender(
			<MantineProvider env="test" theme={testMantineTheme}>
				<ConversationList
					conversations={[archivedConversation]}
					showArchived={true}
					onCreateConversation={vi.fn()}
					onSelect={vi.fn()}
					onToggleCollapse={vi.fn()}
					onToggleShowArchived={vi.fn()}
				/>
			</MantineProvider>,
		);

		expect(screen.getByTestId("conversation-item-archived-1")).toBeTruthy();
	});

	// The row itself stays a plain container — an ARIA button around the actions menu would make those nested
	// controls presentational. The title is the real button, so the keyboard reaches it natively.
	it("makes the conversation title a real button named after the conversation", () => {
		renderWithMantine(
			<ConversationList
				conversations={[conversation({ id: "local-1", title: "A conversation" })]}
				onCreateConversation={vi.fn()}
				onSelect={vi.fn()}
				onToggleCollapse={vi.fn()}
			/>,
		);

		expect(screen.getByRole("button", { name: "A conversation" }).tagName).toBe("BUTTON");
	});

	it("names an untitled conversation rather than exposing a nameless row", () => {
		renderWithMantine(
			<ConversationList
				conversations={[conversation({ id: "local-1", title: "   " })]}
				onCreateConversation={vi.fn()}
				onSelect={vi.fn()}
				onToggleCollapse={vi.fn()}
			/>,
		);

		expect(screen.getByRole("button", { name: "Untitled conversation" })).toBeTruthy();
	});

	// The title button sits inside the row, whose onClick selects the same conversation: without stopPropagation one
	// activation would select twice.
	it("selects the conversation exactly once when the title button is activated", () => {
		const onSelect = vi.fn();
		renderWithMantine(
			<ConversationList
				conversations={[conversation({ id: "local-1", title: "A conversation" })]}
				onCreateConversation={vi.fn()}
				onSelect={onSelect}
				onToggleCollapse={vi.fn()}
			/>,
		);

		fireEvent.click(screen.getByRole("button", { name: "A conversation" }));

		expect(onSelect).toHaveBeenCalledExactlyOnceWith("local-1");
	});

	it("marks the selected conversation with aria-current", () => {
		renderWithMantine(
			<ConversationList
				conversations={[conversation({ id: "local-1", title: "A conversation" })]}
				selectedConversationId="local-1"
				onCreateConversation={vi.fn()}
				onSelect={vi.fn()}
				onToggleCollapse={vi.fn()}
			/>,
		);

		expect(screen.getByRole("button", { name: "A conversation" }).getAttribute("aria-current")).toBe("true");
	});

	// A space is a legal character in a title, and the rename box sits inside the row: typing must never reach the
	// row's own click path.
	it("does not select the conversation when a space is typed in the rename input", async () => {
		const onSelect = vi.fn();
		renderWithMantine(
			<ConversationList
				conversations={[conversation({ id: "local-1", origin: "local", title: "Old title" })]}
				onCreateConversation={vi.fn()}
				onSelect={onSelect}
				onToggleCollapse={vi.fn()}
				onRename={vi.fn()}
			/>,
		);

		fireEvent.click(screen.getByTestId("conversation-actions-local-1"));
		fireEvent.click(await screen.findByTestId("conversation-rename-local-1"));

		const input = await screen.findByTestId("conversation-rename-input-local-1");
		fireEvent.keyDown(input, { key: " " });

		expect(onSelect).not.toHaveBeenCalled();
	});

	// Activating the actions icon opens the menu; it must not also select the row the menu belongs to.
	it("does not select the conversation when the nested actions control is activated", () => {
		const onSelect = vi.fn();
		renderWithMantine(
			<ConversationList
				conversations={[conversation({ id: "local-1", origin: "local" })]}
				onCreateConversation={vi.fn()}
				onSelect={onSelect}
				onToggleCollapse={vi.fn()}
				onDelete={vi.fn()}
			/>,
		);

		fireEvent.click(screen.getByTestId("conversation-actions-local-1"));

		expect(onSelect).not.toHaveBeenCalled();
	});
});

describe("ConversationList truncated page footer", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("offers Show all when the page is truncated and calls onShowAll", () => {
		const onShowAll = vi.fn();
		renderWithMantine(
			<ConversationList
				conversations={[conversation()]}
				truncated={true}
				onShowAll={onShowAll}
				onCreateConversation={vi.fn()}
				onSelect={vi.fn()}
				onToggleCollapse={vi.fn()}
			/>,
		);

		expect(screen.getByText("Showing the 100 most recent")).toBeTruthy();
		fireEvent.click(screen.getByRole("button", { name: "Show all" }));

		expect(onShowAll).toHaveBeenCalledTimes(1);
	});

	it("shows no footer when the page is not truncated", () => {
		renderWithMantine(
			<ConversationList
				conversations={[conversation()]}
				onShowAll={vi.fn()}
				onCreateConversation={vi.fn()}
				onSelect={vi.fn()}
				onToggleCollapse={vi.fn()}
			/>,
		);

		expect(screen.queryByTestId("conversation-list-truncated")).toBeNull();
	});
});

describe("ConversationList new conversation button", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	// It creates an ordinary conversation, so it must not read as the Plain mode beside it.
	it("is labelled New chat and creates a conversation", () => {
		const onCreateConversation = vi.fn();
		renderWithMantine(
			<ConversationList
				conversations={[]}
				onCreateConversation={onCreateConversation}
				onSelect={vi.fn()}
				onToggleCollapse={vi.fn()}
			/>,
		);

		fireEvent.click(screen.getByRole("button", { name: "New chat" }));

		expect(onCreateConversation).toHaveBeenCalledTimes(1);
	});
});

describe("ConversationList export and import", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it.each([
		["markdown", "Export as Markdown"],
		["json", "Export as JSON"],
	] as const)("exports one chat as %s from its menu, a remote one included", async (format, label) => {
		const onExport = vi.fn();
		renderWithMantine(
			<ConversationList
				conversations={[conversation({ id: "remote-1", origin: "remote" })]}
				onCreateConversation={vi.fn()}
				onSelect={vi.fn()}
				onToggleCollapse={vi.fn()}
				onRename={vi.fn()}
				onExport={onExport}
			/>,
		);

		fireEvent.click(screen.getByTestId("conversation-actions-remote-1"));
		fireEvent.click(await screen.findByRole("menuitem", { name: label }));

		expect(onExport).toHaveBeenCalledWith("remote-1", format);
		// Export is a read; the view-only conversation still offers no write.
		expect(screen.queryByTestId("conversation-rename-remote-1")).toBeNull();
	});

	it("exports every chat and opens a JSON picker whose file goes to the import", async () => {
		const onExportAll = vi.fn();
		const onImportFile = vi.fn();
		renderWithMantine(
			<ConversationList
				conversations={[]}
				onCreateConversation={vi.fn()}
				onSelect={vi.fn()}
				onToggleCollapse={vi.fn()}
				onExportAll={onExportAll}
				onImportFile={onImportFile}
			/>,
		);
		const input = screen.getByTestId<HTMLInputElement>("conversation-import-file");
		const click = vi.spyOn(input, "click");

		fireEvent.click(screen.getByRole("button", { name: "Export or import chats" }));
		fireEvent.click(await screen.findByRole("menuitem", { name: "Export all chats (JSON)" }));
		fireEvent.click(screen.getByRole("button", { name: "Export or import chats" }));
		fireEvent.click(await screen.findByRole("menuitem", { name: "Import chats (JSON)" }));
		const picked = new File(["{}"], "chats.chat.json", { type: "application/json" });
		fireEvent.change(input, { target: { files: [picked] } });

		expect(onExportAll).toHaveBeenCalledTimes(1);
		expect(click).toHaveBeenCalledTimes(1);
		expect(input.accept).toBe(".json,application/json");
		expect(onImportFile).toHaveBeenCalledWith(picked);
	});

	it("disables the import while one is running", async () => {
		renderWithMantine(
			<ConversationList
				conversations={[]}
				onCreateConversation={vi.fn()}
				onSelect={vi.fn()}
				onToggleCollapse={vi.fn()}
				onExportAll={vi.fn()}
				onImportFile={vi.fn()}
				importing={true}
			/>,
		);

		fireEvent.click(screen.getByRole("button", { name: "Export or import chats" }));

		expect((await screen.findByRole("menuitem", { name: "Import chats (JSON)" })).hasAttribute("disabled")).toBe(true);
	});

	it("offers no export or import when the page wires none", () => {
		renderWithMantine(
			<ConversationList conversations={[]} onCreateConversation={vi.fn()} onSelect={vi.fn()} onToggleCollapse={vi.fn()} />,
		);

		expect(screen.queryByTestId("conversation-export-import")).toBeNull();
	});
});

describe("ConversationList relative time", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		vi.useFakeTimers({ toFake: ["Date"] });
		vi.setSystemTime(new Date("2026-05-24T12:00:00.000Z"));
	});

	afterEach(async () => {
		cleanup();
		vi.useRealTimers();
		await act(() => i18next.changeLanguage("en"));
	});

	function list(updatedAt: string) {
		return (
			<MantineProvider env="test" theme={testMantineTheme}>
				<ConversationList
					conversations={[conversation({ id: "recent", updatedAt })]}
					onCreateConversation={vi.fn()}
					onSelect={vi.fn()}
					onToggleCollapse={vi.fn()}
				/>
			</MantineProvider>
		);
	}

	it("labels minutes in the UI language and follows a language switch without a remount", async () => {
		render(list("2026-05-24T11:55:00.000Z"));
		expect(screen.getByText("5m ago")).toBeTruthy();

		await act(() => i18next.changeLanguage("de"));

		expect(screen.getByText("vor 5 m")).toBeTruthy();
	});

	it("labels a conversation updated this minute as now, in English and German", async () => {
		render(list("2026-05-24T11:59:50.000Z"));
		expect(screen.getByText("now")).toBeTruthy();

		await act(() => i18next.changeLanguage("de"));

		expect(screen.getByText("jetzt")).toBeTruthy();
	});
});

// The placeholder title is persisted in English and compared by exact string, so only its display is localized.
describe("ConversationList placeholder title", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(async () => {
		cleanup();
		await act(() => i18next.changeLanguage("en"));
	});

	async function renderGerman(
		collapsed: boolean,
		searchQuery?: string,
		onRename = vi.fn(),
		searchResults?: ChatConversationSearchModel,
	) {
		// The app loads the German bundle lazily after the switch; preloading it keeps the first render from racing it.
		i18next.addResourceBundle("de", "translation", de, true, true);
		await act(() => i18next.changeLanguage("de"));
		renderWithMantine(
			<ConversationList
				conversations={[
					conversation({ id: "placeholder", title: "New conversation" }),
					conversation({ id: "named" }),
					// What the chat mapper stores for a conversation the server returns without a title.
					conversation({ id: "untitled", title: "Untitled conversation" }),
					conversation({ id: "archived-placeholder", title: "New conversation", isArchived: true }),
				]}
				collapsed={collapsed}
				searchQuery={searchQuery}
				searchResults={searchResults}
				onCreateConversation={vi.fn()}
				onSelect={vi.fn()}
				onToggleCollapse={vi.fn()}
				onSearchChange={vi.fn()}
				onRename={onRename}
			/>,
		);
	}

	it("finds the placeholder conversation by its displayed title", async () => {
		await renderGerman(false, "neuer chat");

		expect(screen.getByTestId("conversation-item-placeholder")).toBeTruthy();
		expect(screen.queryByTestId("conversation-item-named")).toBeNull();
		expect(screen.queryByTestId("conversation-item-archived-placeholder")).toBeNull();
	});

	// The server searches the stored English title, so its answer cannot contain a match on the localized one.
	it("keeps a displayed-title match after an empty server search answer arrives", async () => {
		await renderGerman(false, "neuer chat", vi.fn(), { conversations: [], indexing: false });

		expect(screen.getByTestId("conversation-item-placeholder").textContent).toContain(de.chat.search.titleMatch);
		expect(screen.queryByTestId("conversation-item-named")).toBeNull();
		expect(screen.queryByTestId("conversation-item-untitled")).toBeNull();
		expect(screen.queryByTestId("conversation-item-archived-placeholder")).toBeNull();
	});

	it("lists a displayed-title match the server also found only once, with the server's snippet", async () => {
		const hit = conversation({ id: "placeholder", title: "New conversation", lastMessagePreview: "…Neuer Chat im Text…" });
		await renderGerman(false, "neuer chat", vi.fn(), { conversations: [hit], indexing: false });

		expect(screen.getAllByTestId("conversation-item-placeholder")).toHaveLength(1);
		expect(screen.getByTestId("conversation-item-placeholder").textContent).toContain("Neuer Chat im Text");
	});

	// Prefilling the displayed text would save "Neuer Chat" on an unedited rename and end the automatic titling.
	it("starts the placeholder's rename empty and saves nothing when it stays empty", async () => {
		const onRename = vi.fn();
		await renderGerman(false, undefined, onRename);

		fireEvent.click(screen.getByTestId("conversation-actions-placeholder"));
		fireEvent.click(await screen.findByTestId("conversation-rename-placeholder"));
		const input = (await screen.findByTestId("conversation-rename-input-placeholder")) as HTMLInputElement;
		expect(input.value).toBe("");

		fireEvent.keyDown(input, { key: "Enter" });

		expect(onRename).not.toHaveBeenCalled();
		expect(screen.getByTestId("conversation-item-placeholder").textContent).toContain("Neuer Chat");
	});

	it("shows the placeholder title in the UI language in the expanded list", async () => {
		await renderGerman(false);

		expect(screen.getByTestId("conversation-item-placeholder").textContent).toContain("Neuer Chat");
		expect(screen.getByTestId("conversation-item-placeholder").textContent).not.toContain("New conversation");
		expect(screen.getByTestId("conversation-item-named").textContent).toContain("A conversation");
		expect(screen.getByTestId("conversation-item-untitled").textContent).toContain("Unbenannter Chat");
	});

	it("labels the placeholder title in the UI language in the collapsed rail", async () => {
		await renderGerman(true);

		expect(screen.getByTestId("conversation-item-placeholder").getAttribute("aria-label")).toBe("Neuer Chat");
		expect(screen.getByTestId("conversation-item-named").getAttribute("aria-label")).toBe("A conversation");
		expect(screen.getByTestId("conversation-item-untitled").getAttribute("aria-label")).toBe("Unbenannter Chat");
	});

	it("starts an untitled conversation's rename empty", async () => {
		await renderGerman(false);

		fireEvent.click(screen.getByTestId("conversation-actions-untitled"));
		fireEvent.click(await screen.findByTestId("conversation-rename-untitled"));

		expect(((await screen.findByTestId("conversation-rename-input-untitled")) as HTMLInputElement).value).toBe("");
	});
});
