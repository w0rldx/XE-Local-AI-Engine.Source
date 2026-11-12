// @vitest-environment jsdom

import { act, cleanup, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { HubConnectionStatusChip } from "@/core/layout/components/HubConnectionStatusChip/HubConnectionStatusChip";
import { renderWithProviders } from "@/test/RenderWithProviders";

// Mirrors HUB_RECONNECTING_CHIP_DELAY_MS in the component (a component module exports components only).
const HUB_RECONNECTING_CHIP_DELAY_MS = 2_000;

// The shared manager's degraded set, driven by the test; the manager's own bookkeeping is tested beside it.
const hub = vi.hoisted(() => {
	const listeners = new Set<() => void>();
	const state = { degraded: false };
	return {
		state,
		listeners,
		setDegraded(degraded: boolean) {
			state.degraded = degraded;
			for (const listener of listeners) {
				listener();
			}
		},
	};
});

vi.mock("@/core/api/signalr/SharedHubConnection", () => ({
	hasDegradedHub: () => hub.state.degraded,
	subscribeDegradedHubs: (listener: () => void) => {
		hub.listeners.add(listener);
		return () => hub.listeners.delete(listener);
	},
}));

beforeEach(() => {
	vi.useFakeTimers();
	hub.state.degraded = false;
});

afterEach(() => {
	cleanup();
	vi.useRealTimers();
});

describe("HubConnectionStatusChip", () => {
	it("shows once a hub has been down past the blip delay and hides when it is back", () => {
		renderWithProviders(<HubConnectionStatusChip />);
		expect(screen.getByRole("status").textContent).toBe("");

		act(() => hub.setDegraded(true));
		act(() => vi.advanceTimersByTime(HUB_RECONNECTING_CHIP_DELAY_MS));
		expect(screen.getByTestId("hub-connection-status-chip").textContent).toBe("Live updates reconnecting…");
		expect(screen.getByRole("status").getAttribute("aria-live")).toBe("polite");

		act(() => hub.setDegraded(false));
		expect(screen.queryByTestId("hub-connection-status-chip")).toBeNull();
	});

	it("never shows for a drop shorter than the blip delay", () => {
		renderWithProviders(<HubConnectionStatusChip />);

		act(() => hub.setDegraded(true));
		act(() => vi.advanceTimersByTime(HUB_RECONNECTING_CHIP_DELAY_MS - 1));
		act(() => hub.setDegraded(false));
		act(() => vi.advanceTimersByTime(HUB_RECONNECTING_CHIP_DELAY_MS));

		expect(screen.queryByTestId("hub-connection-status-chip")).toBeNull();
	});
});
