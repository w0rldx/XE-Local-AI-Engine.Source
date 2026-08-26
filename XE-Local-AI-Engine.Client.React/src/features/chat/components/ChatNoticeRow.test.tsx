// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ChatNoticeRow } from "@/features/chat/components/ChatNoticeRow";
import type { ChatNoticePart } from "@/features/chat/models/ChatModels";

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

function noticePart(overrides: Partial<ChatNoticePart> = {}): ChatNoticePart {
	return {
		kind: "notice",
		id: "assistant-1:5",
		sequence: 5,
		noticeKind: "ModelSubstituted",
		text: "Switched to a smaller model.",
		...overrides,
	};
}

describe("ChatNoticeRow", () => {
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

	it("renders the server-provided notice message verbatim", () => {
		renderWithProviders(<ChatNoticeRow part={noticePart({ text: "Switched to a smaller model." })} />);

		expect(screen.getByText("Switched to a smaller model.")).toBeTruthy();
	});

	it("tags the row with the notice kind for each known kind", () => {
		const { rerender } = renderWithProviders(<ChatNoticeRow part={noticePart({ noticeKind: "ModelSubstituted" })} />);
		expect(screen.getByTestId("chat-notice-row").getAttribute("data-notice-kind")).toBe("ModelSubstituted");

		rerender(
			<MantineProvider>
				<ChatNoticeRow part={noticePart({ noticeKind: "ToolDisabled", text: "A tool was disabled." })} />
			</MantineProvider>,
		);
		expect(screen.getByTestId("chat-notice-row").getAttribute("data-notice-kind")).toBe("ToolDisabled");

		rerender(
			<MantineProvider>
				<ChatNoticeRow part={noticePart({ noticeKind: "HistoryTruncated", text: "Older history was trimmed." })} />
			</MantineProvider>,
		);
		expect(screen.getByTestId("chat-notice-row").getAttribute("data-notice-kind")).toBe("HistoryTruncated");
	});

	it("renders the orchestration-degraded notice with its own kind tag and server text", () => {
		// An orchestrator that ran as a single agent must be visible in the turn, not just in a server log.
		renderWithProviders(
			<ChatNoticeRow
				part={noticePart({
					noticeKind: "OrchestrationDegraded",
					text: "Orchestration was not used for this turn: the model for this turn cannot call tools. The agent ran as a single agent instead.",
				})}
			/>,
		);

		expect(screen.getByTestId("chat-notice-row").getAttribute("data-notice-kind")).toBe("OrchestrationDegraded");
		expect(
			screen.getByText(
				"Orchestration was not used for this turn: the model for this turn cannot call tools. The agent ran as a single agent instead.",
			),
		).toBeTruthy();
	});

	it("falls back gracefully for an unknown/forward-compat notice kind", () => {
		renderWithProviders(<ChatNoticeRow part={noticePart({ noticeKind: "SomethingNew", text: "A new kind of notice." })} />);

		expect(screen.getByText("A new kind of notice.")).toBeTruthy();
		expect(screen.getByTestId("chat-notice-row").getAttribute("data-notice-kind")).toBe("SomethingNew");
	});
});
