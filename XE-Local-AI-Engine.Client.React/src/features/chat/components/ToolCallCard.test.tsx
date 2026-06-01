// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ToolCallCard } from "@/features/chat/components/ToolCallCard";
import type { ChatToolPart } from "@/features/chat/models/ChatModels";

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

function toolPart(overrides: Partial<ChatToolPart> = {}): ChatToolPart {
	return { kind: "tool", id: "call-1", sequence: 1, name: "get_time", state: "received", ...overrides };
}

describe("ToolCallCard", () => {
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

	it("shows the tool name and a live label while requesting", () => {
		renderWithProviders(<ToolCallCard part={toolPart({ state: "requesting", args: '{"tz":"utc"}' })} />);

		const card = screen.getByTestId("chat-tool-call-card-get_time");
		expect(card.getAttribute("data-state")).toBe("requesting");
		expect(screen.getByText("get_time")).toBeTruthy();
		expect(screen.getByText("live")).toBeTruthy();
		// Args render even while live so the call is legible before the result arrives.
		expect(card.textContent).toContain("tz");
	});

	it("renders the result body inline once received", () => {
		renderWithProviders(<ToolCallCard part={toolPart({ state: "received", result: '{"time":"12:00"}' })} />);

		const result = screen.getByTestId("chat-tool-call-result-get_time");
		expect(result.textContent).toContain("12:00");
		expect(screen.getByText("Result")).toBeTruthy();
	});

	it("pretty-prints JSON args and passes non-JSON through unchanged", () => {
		renderWithProviders(<ToolCallCard part={toolPart({ state: "received", args: '{"a":1}', result: "plain text" })} />);

		// JSON args are indented across lines; the plain result is shown verbatim.
		expect(screen.getByTestId("chat-tool-call-result-get_time").textContent).toBe("plain text");
		expect(screen.getByText("Arguments")).toBeTruthy();
	});

	it("uses error styling and label on a failed tool call", () => {
		renderWithProviders(<ToolCallCard part={toolPart({ state: "failed", result: "boom" })} />);

		const card = screen.getByTestId("chat-tool-call-card-get_time");
		expect(card.getAttribute("data-state")).toBe("failed");
		expect(screen.getByText("Error")).toBeTruthy();
		expect(screen.getByTestId("chat-tool-call-result-get_time").textContent).toBe("boom");
	});

	it("surfaces an approval indicator when the tool requires approval", () => {
		renderWithProviders(<ToolCallCard part={toolPart({ state: "waiting", requiresApproval: true })} />);

		expect(screen.getByTestId("chat-tool-call-approval-get_time")).toBeTruthy();
	});

	it("shows a muted '(no output)' affordance when a received tool call has an empty result", () => {
		renderWithProviders(<ToolCallCard part={toolPart({ state: "received", result: undefined })} />);

		expect(screen.getByTestId("chat-tool-call-no-output-get_time")).toBeTruthy();
		// The result body must not render alongside the no-output notice.
		expect(screen.queryByTestId("chat-tool-call-result-get_time")).toBeNull();
	});

	it("does not show the no-output affordance while the tool is still in flight", () => {
		renderWithProviders(<ToolCallCard part={toolPart({ state: "requesting", result: undefined })} />);

		expect(screen.queryByTestId("chat-tool-call-no-output-get_time")).toBeNull();
	});
});
