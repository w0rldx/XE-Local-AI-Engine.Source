// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { act, cleanup, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { NodeChatConnectionStatus } from "@/features/chat/api/NodeChatConnection";

// A controllable stand-in for the shared chat-hub connection singleton: the test drives status transitions through
// `emit` and asserts the chip reacts, and checks the subscription is torn down on unmount.
const { chatConnectionMock } = vi.hoisted(() => {
	let status: NodeChatConnectionStatus = "connecting";
	const listeners = new Set<{ onStatusChange?: (status: NodeChatConnectionStatus) => void }>();
	const unsubscribeSpy = vi.fn();
	return {
		chatConnectionMock: {
			listeners,
			unsubscribeSpy,
			get status(): NodeChatConnectionStatus {
				return status;
			},
			setStatus(next: NodeChatConnectionStatus): void {
				status = next;
			},
			emit(next: NodeChatConnectionStatus): void {
				status = next;
				for (const listener of listeners) {
					listener.onStatusChange?.(next);
				}
			},
			subscribe: vi.fn((events: { onStatusChange?: (status: NodeChatConnectionStatus) => void }) => {
				listeners.add(events);
				return () => {
					unsubscribeSpy();
					listeners.delete(events);
				};
			}),
		},
	};
});

vi.mock("@/features/chat/api/NodeChatConnection", () => ({ nodeChatConnection: chatConnectionMock }));

import { ChatConnectionStatusChip } from "@/features/chat/components/ChatConnectionStatusChip";

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

describe("ChatConnectionStatusChip", () => {
	beforeEach(() => {
		chatConnectionMock.setStatus("connecting");
		chatConnectionMock.listeners.clear();
		chatConnectionMock.unsubscribeSpy.mockReset();
		chatConnectionMock.subscribe.mockClear();
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
			value: vi.fn().mockImplementation(() => ({ observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() })),
		});
	});

	afterEach(() => {
		cleanup();
	});

	it("is hidden while connected", () => {
		chatConnectionMock.setStatus("connected");
		renderWithProviders(<ChatConnectionStatusChip />);

		expect(screen.queryByTestId("chat-connection-status-chip")).toBeNull();
	});

	it("shows the reconnecting chip on a reconnecting status and hides it once reconnected", () => {
		chatConnectionMock.setStatus("connected");
		renderWithProviders(<ChatConnectionStatusChip />);
		expect(screen.queryByTestId("chat-connection-status-chip")).toBeNull();

		act(() => chatConnectionMock.emit("reconnecting"));
		const chip = screen.getByTestId("chat-connection-status-chip");
		expect(chip.getAttribute("data-status")).toBe("reconnecting");

		act(() => chatConnectionMock.emit("connected"));
		expect(screen.queryByTestId("chat-connection-status-chip")).toBeNull();
	});

	it("shows Offline only after the hub has connected at least once", () => {
		// Never connected yet: a disconnected state on initial load must not flash the chip.
		chatConnectionMock.setStatus("disconnected");
		renderWithProviders(<ChatConnectionStatusChip />);
		expect(screen.queryByTestId("chat-connection-status-chip")).toBeNull();

		// Once connected then dropped mid-session, the Offline chip appears.
		act(() => chatConnectionMock.emit("connected"));
		act(() => chatConnectionMock.emit("disconnected"));
		const chip = screen.getByTestId("chat-connection-status-chip");
		expect(chip.getAttribute("data-status")).toBe("disconnected");
	});

	it("unsubscribes from the connection on unmount", () => {
		chatConnectionMock.setStatus("connected");
		const view = renderWithProviders(<ChatConnectionStatusChip />);
		expect(chatConnectionMock.subscribe).toHaveBeenCalledTimes(1);

		view.unmount();
		expect(chatConnectionMock.unsubscribeSpy).toHaveBeenCalledTimes(1);
	});
});
