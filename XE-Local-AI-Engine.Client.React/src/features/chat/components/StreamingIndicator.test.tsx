// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { act, cleanup, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The elapsed-time assertions read an interpolated string, so react-i18next needs the app's own initialised
// instance registered as its default (see the header of src/test/RenderWithProviders.tsx). Importing it does
// NOT change this file's bare-MantineProvider wrapper; without it `t` returns "{{elapsed}}" uninterpolated.
import "@/i18n";
import { StreamingIndicator } from "@/features/chat/components/StreamingIndicator";

function renderBare(ui: ReactElement) {
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
		renderBare(<StreamingIndicator isActive={true} isQueued={true} hasContent={false} />);

		expect(screen.getByTestId("chat-stream-queued-indicator")).toBeTruthy();
		expect(screen.queryByTestId("chat-streaming-indicator")).toBeNull();
		expect(screen.queryByTestId("chat-stream-delayed-indicator")).toBeNull();
	});

	it("shows the streaming indicator once content is flowing and no longer queued", () => {
		renderBare(<StreamingIndicator isActive={true} isQueued={false} hasContent={true} />);

		expect(screen.getByTestId("chat-streaming-indicator")).toBeTruthy();
		expect(screen.queryByTestId("chat-stream-queued-indicator")).toBeNull();
	});

	// Errors moved out of the footer: a failed turn now renders its error block inside the assistant bubble
	// (ChatMessage) so it shows exactly once and survives reload. The footer never renders an error here.
	it("never renders an error in the footer", () => {
		renderBare(<StreamingIndicator isActive={false} isQueued={false} hasContent={false} />);

		expect(screen.queryByTestId("chat-stream-error")).toBeNull();
	});

	// A local cold load surfaces a distinct "Loading model…" indicator before the first token, so the wait
	// reads as legitimate progress rather than an apparent hang.
	it("shows the model-loading indicator during the loading_model phase (before the first token)", () => {
		renderBare(<StreamingIndicator isActive={true} isQueued={false} hasContent={false} runtimePhase="loading_model" />);

		expect(screen.getByTestId("chat-stream-loading-model-indicator")).toBeTruthy();
	});

	it("shows the model-loading indicator during the preparing_runtime phase", () => {
		renderBare(<StreamingIndicator isActive={true} isQueued={false} hasContent={false} runtimePhase="preparing_runtime" />);

		expect(screen.getByTestId("chat-stream-loading-model-indicator")).toBeTruthy();
	});

	it("stops showing the model-loading indicator once generation begins (generating phase)", () => {
		renderBare(<StreamingIndicator isActive={true} isQueued={false} hasContent={false} runtimePhase="generating" />);

		expect(screen.queryByTestId("chat-stream-loading-model-indicator")).toBeNull();
	});

	it("stops showing the model-loading indicator once content has streamed, even if a phase lingers", () => {
		renderBare(<StreamingIndicator isActive={true} isQueued={false} hasContent={true} runtimePhase="loading_model" />);

		expect(screen.queryByTestId("chat-stream-loading-model-indicator")).toBeNull();
	});

	it("prefers the queued affordance over the model-loading indicator when both apply", () => {
		renderBare(<StreamingIndicator isActive={true} isQueued={true} hasContent={false} runtimePhase="loading_model" />);

		expect(screen.getByTestId("chat-stream-queued-indicator")).toBeTruthy();
		expect(screen.queryByTestId("chat-stream-loading-model-indicator")).toBeNull();
	});
	// R12: a cold load shows how long it has been going, anchored on the SERVER phase timestamp so a page
	// reload mid-load resumes the count instead of restarting it. No percentage, no bar, no estimate.
	describe("cold-load elapsed time", () => {
		beforeEach(() => {
			vi.useFakeTimers();
			vi.setSystemTime(new Date("2026-09-10T08:15:12.000Z"));
		});

		afterEach(() => {
			vi.useRealTimers();
		});

		function elapsedText() {
			return screen.getByTestId("chat-stream-loading-model-elapsed").textContent ?? "";
		}

		it("renders elapsed time anchored on the server phase timestamp, not on first render", () => {
			renderBare(
				<StreamingIndicator
					isActive={true}
					isQueued={false}
					hasContent={false}
					runtimePhase="loading_model"
					runtimePhaseChangedAtUtc="2026-09-10T08:15:00.000Z"
				/>,
			);

			// Twelve seconds already elapsed server-side before this component ever mounted.
			expect(elapsedText()).toContain("12s");
		});

		it("advances the elapsed time once per second while the model is loading", async () => {
			renderBare(
				<StreamingIndicator
					isActive={true}
					isQueued={false}
					hasContent={false}
					runtimePhase="loading_model"
					runtimePhaseChangedAtUtc="2026-09-10T08:15:12.000Z"
				/>,
			);

			expect(elapsedText()).toContain("0s");

			await act(async () => {
				await vi.advanceTimersByTimeAsync(1_000);
			});
			expect(elapsedText()).toContain("1s");

			// Past a minute the compact formatter switches shape; the counter must keep tracking through it.
			await act(async () => {
				await vi.advanceTimersByTimeAsync(64_000);
			});
			expect(elapsedText()).toContain("1m 05s");
		});

		it("falls back to first-observed time when the server phase timestamp is absent", async () => {
			renderBare(<StreamingIndicator isActive={true} isQueued={false} hasContent={false} runtimePhase="loading_model" />);

			expect(elapsedText()).toContain("0s");

			await act(async () => {
				await vi.advanceTimersByTimeAsync(5_000);
			});
			expect(elapsedText()).toContain("5s");
		});

		it("clears the 1 Hz interval when the loading phase ends", async () => {
			const clearIntervalSpy = vi.spyOn(window, "clearInterval");
			const view = renderBare(
				<StreamingIndicator
					isActive={true}
					isQueued={false}
					hasContent={false}
					runtimePhase="loading_model"
					runtimePhaseChangedAtUtc="2026-09-10T08:15:12.000Z"
				/>,
			);
			const clearsAfterMount = clearIntervalSpy.mock.calls.length;

			view.rerender(
				<MantineProvider>
					<StreamingIndicator
						isActive={true}
						isQueued={false}
						hasContent={false}
						runtimePhase="generating"
						runtimePhaseChangedAtUtc="2026-09-10T08:15:12.000Z"
					/>
				</MantineProvider>,
			);
			expect(clearIntervalSpy.mock.calls.length).toBeGreaterThan(clearsAfterMount);

			// A queued turn that still carries a loading phase must not arm the ticker either: the queued branch
			// returns first, so a running interval there would re-render every second behind a static badge.
			view.rerender(
				<MantineProvider>
					<StreamingIndicator
						isActive={true}
						isQueued={false}
						hasContent={false}
						runtimePhase="loading_model"
						runtimePhaseChangedAtUtc="2026-09-10T08:15:12.000Z"
					/>
				</MantineProvider>,
			);
			const clearsBeforeQueued = clearIntervalSpy.mock.calls.length;

			view.rerender(
				<MantineProvider>
					<StreamingIndicator
						isActive={true}
						isQueued={true}
						hasContent={false}
						runtimePhase="loading_model"
						runtimePhaseChangedAtUtc="2026-09-10T08:15:12.000Z"
					/>
				</MantineProvider>,
			);
			expect(clearIntervalSpy.mock.calls.length).toBeGreaterThan(clearsBeforeQueued);
			expect(screen.queryByTestId("chat-stream-loading-model-elapsed")).toBeNull();

			clearIntervalSpy.mockRestore();
		});
	});
});
