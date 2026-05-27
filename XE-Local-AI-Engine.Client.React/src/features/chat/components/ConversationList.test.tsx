// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ConversationList } from "@/features/chat/components/ConversationList";
import type { ChatConversationModel } from "@/features/chat/models/ChatModels";

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
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
			<MantineProvider>
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
});
