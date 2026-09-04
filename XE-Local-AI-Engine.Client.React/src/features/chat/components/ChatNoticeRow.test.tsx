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

	it("renders the tools-filtered notice with its own kind tag and whatever text the server sent", () => {
		// The sentence itself is the server's and is pinned backend-side (InvocationRunnerTests asserts the wording
		// against BuildToolsFilteredNoticeMessage). A copy of it here would drift silently, so this uses a stand-in
		// string: what the component owns is the kind tag and rendering the server text verbatim.
		const serverText = "a server-owned counts-only sentence";
		const { container } = renderWithProviders(<ChatNoticeRow part={noticePart({ noticeKind: "ToolsFiltered", text: serverText })} />);

		expect(screen.getByTestId("chat-notice-row").getAttribute("data-notice-kind")).toBe("ToolsFiltered");
		expect(screen.getByText(serverText)).toBeTruthy();
		// Its own glyph: sharing ToolDisabled's would render an optimisation and a degradation identically.
		expect(container.querySelector(".tabler-icon-filter")).toBeTruthy();
		expect(container.querySelector(".tabler-icon-tools-off")).toBeNull();
	});

	it("falls back gracefully for an unknown/forward-compat notice kind", () => {
		renderWithProviders(<ChatNoticeRow part={noticePart({ noticeKind: "SomethingNew", text: "A new kind of notice." })} />);

		expect(screen.getByText("A new kind of notice.")).toBeTruthy();
		expect(screen.getByTestId("chat-notice-row").getAttribute("data-notice-kind")).toBe("SomethingNew");
	});
});
