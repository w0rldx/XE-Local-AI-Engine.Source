// @vitest-environment jsdom

// The result panel is the one place a run's own output is shown. That output is the End node's resolved result value
// itself, so what is pinned is the reading of it: a string reads as Markdown, anything else as the read-only JSON
// document, no output renders nothing, and the raw result is always one click from the clipboard.

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

// Monaco needs a layout engine jsdom does not have; the document's CONTENT is what this file pins.
vi.mock("@/core/ui/components/CodeEditor/CodeEditor", () => ({
	CodeEditor: ({ value, "data-testid": testId }: { value: string; "data-testid"?: string }) => (
		<textarea data-testid={testId} readOnly={true} value={value} />
	),
}));

import { GraphWorkflowRunResultPanel } from "@/features/graphWorkflows/components/GraphWorkflowRunResultPanel";
import { renderWithProviders } from "@/test/RenderWithProviders";

describe("GraphWorkflowRunResultPanel", () => {
	afterEach(() => {
		cleanup();
		vi.restoreAllMocks();
	});

	it("renders a string result as Markdown", () => {
		const report = "# Bericht\n\n**neu**";
		renderWithProviders(<GraphWorkflowRunResultPanel output={report} />);

		const markdown = screen.getByTestId("graph-workflow-run-result-markdown");
		expect(markdown.querySelector("h1")?.textContent).toBe("Bericht");
		expect(markdown.querySelector("strong")?.textContent).toBe("neu");
		expect(screen.queryByTestId("graph-workflow-run-result-json")).toBeNull();
	});

	// The report is model output built from a fetched page, so an image in it must not fetch: its src would be a beacon.
	it("writes a Markdown image as its alt text and URL and never renders an <img>", () => {
		const report = "# Bericht\n\n![Grundriss](https://tracker.example/pixel.png?d=secret)";
		renderWithProviders(<GraphWorkflowRunResultPanel output={report} />);

		const markdown = screen.getByTestId("graph-workflow-run-result-markdown");
		expect(markdown.querySelector("img")).toBeNull();
		expect(markdown.querySelector("a")).toBeNull();
		expect(markdown.textContent).toContain("[Grundriss] https://tracker.example/pixel.png?d=secret");
	});

	it("renders an object result as the read-only JSON document", () => {
		renderWithProviders(<GraphWorkflowRunResultPanel output={{ count: 2 }} />);

		expect((screen.getByTestId("graph-workflow-run-result-json") as HTMLTextAreaElement).value).toBe(
			JSON.stringify({ count: 2 }, null, 2),
		);
		expect(screen.queryByTestId("graph-workflow-run-result-markdown")).toBeNull();
	});

	it("renders nothing when the run carries no output", () => {
		for (const output of [undefined, null]) {
			const { container } = renderWithProviders(<GraphWorkflowRunResultPanel output={output} />);
			expect(container.querySelector("[data-testid='graph-workflow-run-result']")).toBeNull();
			cleanup();
		}
	});

	it("copies the raw result, not the rendered Markdown", async () => {
		const writeText = vi.fn().mockResolvedValue(undefined);
		Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
		renderWithProviders(<GraphWorkflowRunResultPanel output="# Bericht" />);

		fireEvent.click(screen.getByTestId("graph-workflow-run-result-copy"));

		await waitFor(() => expect(writeText).toHaveBeenCalledWith("# Bericht"));
	});
});
