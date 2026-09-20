// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ConversationList } from "@/features/chat/components/ConversationList";
import type { ChatConversationModel } from "@/features/chat/models/ChatModels";

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider env="test">{ui}</MantineProvider>);
}

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

function installJsdomEnvironmentMocks(): void {
	Object.defineProperty(window, "matchMedia", {
		writable: true,
		value: vi.fn().mockImplementation((query: string) => ({
			matches: false,
			media: query,
			onchange: null,
			addEventListener: vi.fn(),
			removeEventListener: vi.fn(),
			dispatchEvent: vi.fn(),
		})),
	});
	Object.defineProperty(window, "ResizeObserver", {
		writable: true,
		value: class ResizeObserverMock {
			observe = vi.fn();

			unobserve = vi.fn();

			disconnect = vi.fn();
		},
	});
}

describe("ConversationList origin badge", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("renders a Remote badge for remote-origin conversations", () => {
		renderWithProviders(
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
		renderWithProviders(
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
		renderWithProviders(
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
		renderWithProviders(
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
		renderWithProviders(
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
		renderWithProviders(
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
		renderWithProviders(
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
		renderWithProviders(
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
		renderWithProviders(
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
		renderWithProviders(
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

	it("filters conversations by the search query against title and preview", () => {
		renderWithProviders(
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

	it("hides archived conversations until show-archived is enabled", () => {
		const archivedConversation = conversation({ id: "archived-1", isArchived: true });
		const { rerender } = renderWithProviders(
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
			<MantineProvider env="test">
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
		renderWithProviders(
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
		renderWithProviders(
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
		renderWithProviders(
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
		renderWithProviders(
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
		renderWithProviders(
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
		renderWithProviders(
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
