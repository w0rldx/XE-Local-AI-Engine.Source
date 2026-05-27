// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { StreamingIndicator } from "@/features/chat/components/StreamingIndicator";

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

describe("StreamingIndicator", () => {
	beforeEach(() => {
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
	});

	afterEach(() => {
		cleanup();
	});

	it("shows a distinct queued affordance (not the typing indicator) when queued", () => {
		renderWithProviders(<StreamingIndicator isActive={true} isQueued={true} hasContent={false} />);

		expect(screen.getByTestId("chat-stream-queued-indicator")).toBeTruthy();
		expect(screen.queryByTestId("chat-streaming-indicator")).toBeNull();
		expect(screen.queryByTestId("chat-stream-delayed-indicator")).toBeNull();
	});

	it("shows the streaming indicator once content is flowing and no longer queued", () => {
		renderWithProviders(<StreamingIndicator isActive={true} isQueued={false} hasContent={true} />);

		expect(screen.getByTestId("chat-streaming-indicator")).toBeTruthy();
		expect(screen.queryByTestId("chat-stream-queued-indicator")).toBeNull();
	});

	it("renders an error with its failure category over any queued/streaming state", () => {
		renderWithProviders(<StreamingIndicator isActive={true} isQueued={true} error="timed out" failureCategory="inter-chunk-stall" />);

		expect(screen.getByTestId("chat-stream-error")).toBeTruthy();
		expect(screen.queryByTestId("chat-stream-queued-indicator")).toBeNull();
	});
});
