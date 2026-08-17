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

	// Errors moved out of the footer: a failed turn now renders its error block inside the assistant bubble
	// (ChatMessage) so it shows exactly once and survives reload. The footer never renders an error here.
	it("never renders an error in the footer", () => {
		renderWithProviders(<StreamingIndicator isActive={false} isQueued={false} hasContent={false} />);

		expect(screen.queryByTestId("chat-stream-error")).toBeNull();
	});

	// A local cold load surfaces a distinct "Loading model…" indicator before the first token, so the wait
	// reads as legitimate progress rather than an apparent hang.
	it("shows the model-loading indicator during the loading_model phase (before the first token)", () => {
		renderWithProviders(<StreamingIndicator isActive={true} isQueued={false} hasContent={false} runtimePhase="loading_model" />);

		expect(screen.getByTestId("chat-stream-loading-model-indicator")).toBeTruthy();
	});

	it("shows the model-loading indicator during the preparing_runtime phase", () => {
		renderWithProviders(<StreamingIndicator isActive={true} isQueued={false} hasContent={false} runtimePhase="preparing_runtime" />);

		expect(screen.getByTestId("chat-stream-loading-model-indicator")).toBeTruthy();
	});

	it("stops showing the model-loading indicator once generation begins (generating phase)", () => {
		renderWithProviders(<StreamingIndicator isActive={true} isQueued={false} hasContent={false} runtimePhase="generating" />);

		expect(screen.queryByTestId("chat-stream-loading-model-indicator")).toBeNull();
	});

	it("stops showing the model-loading indicator once content has streamed, even if a phase lingers", () => {
		renderWithProviders(<StreamingIndicator isActive={true} isQueued={false} hasContent={true} runtimePhase="loading_model" />);

		expect(screen.queryByTestId("chat-stream-loading-model-indicator")).toBeNull();
	});

	it("prefers the queued affordance over the model-loading indicator when both apply", () => {
		renderWithProviders(<StreamingIndicator isActive={true} isQueued={true} hasContent={false} runtimePhase="loading_model" />);

		expect(screen.getByTestId("chat-stream-queued-indicator")).toBeTruthy();
		expect(screen.queryByTestId("chat-stream-loading-model-indicator")).toBeNull();
	});
});
