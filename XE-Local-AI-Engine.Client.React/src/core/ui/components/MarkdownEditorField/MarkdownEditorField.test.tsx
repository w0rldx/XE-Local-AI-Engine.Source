// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { MarkdownEditorField } from "@/core/ui/components/MarkdownEditorField/MarkdownEditorField";

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

describe("MarkdownEditorField", () => {
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
		Object.defineProperty(window, "ResizeObserver", {
			writable: true,
			value: class ResizeObserverMock {
				observe = vi.fn();
				unobserve = vi.fn();
				disconnect = vi.fn();
			},
		});
		// jsdom lacks FontFaceSet API that Mantine's autosize Textarea subscribes to.
		Object.defineProperty(document, "fonts", {
			configurable: true,
			value: { addEventListener: vi.fn(), removeEventListener: vi.fn() },
		});
	});

	afterEach(() => {
		cleanup();
	});

	it("renders a textarea in edit mode by default", () => {
		renderWithProviders(<MarkdownEditorField value="hello" onChange={vi.fn()} data-testid="mef" />);
		expect(screen.getByTestId("mef-textarea")).toBeTruthy();
	});

	it("calls onChange when the textarea value changes", () => {
		const onChange = vi.fn();
		renderWithProviders(<MarkdownEditorField value="" onChange={onChange} data-testid="mef" />);
		fireEvent.change(screen.getByTestId("mef-textarea"), { target: { value: "new text" } });
		expect(onChange).toHaveBeenCalledWith("new text");
	});

	it("switches to preview pane when Preview segment is clicked", () => {
		renderWithProviders(<MarkdownEditorField value="**bold**" onChange={vi.fn()} data-testid="mef" />);
		fireEvent.click(screen.getByText("Preview"));
		expect(screen.getByTestId("mef-preview")).toBeTruthy();
		// Textarea should no longer be in the DOM
		expect(screen.queryByTestId("mef-textarea")).toBeNull();
	});

	it("renders markdown content in preview mode", () => {
		renderWithProviders(<MarkdownEditorField value="Hello **world**" onChange={vi.fn()} data-testid="mef" />);
		fireEvent.click(screen.getByText("Preview"));
		// The preview pane should contain the rendered text
		expect(screen.getByTestId("mef-preview").textContent).toContain("Hello");
		expect(screen.getByTestId("mef-preview").textContent).toContain("world");
	});

	it("switches back to edit mode when Edit segment is clicked", () => {
		renderWithProviders(<MarkdownEditorField value="text" onChange={vi.fn()} data-testid="mef" />);
		fireEvent.click(screen.getByText("Preview"));
		fireEvent.click(screen.getByText("Edit"));
		expect(screen.getByTestId("mef-textarea")).toBeTruthy();
		expect(screen.queryByTestId("mef-preview")).toBeNull();
	});

	it("renders label and required marker in preview mode", () => {
		renderWithProviders(
			<MarkdownEditorField value="" onChange={vi.fn()} label="Instructions" required={true} data-testid="mef" />,
		);
		fireEvent.click(screen.getByText("Preview"));
		expect(screen.getByText("Instructions")).toBeTruthy();
		expect(screen.getByText("*")).toBeTruthy();
	});

	it("renders an error message in preview mode", () => {
		renderWithProviders(<MarkdownEditorField value="" onChange={vi.fn()} error="Required field" data-testid="mef" />);
		fireEvent.click(screen.getByText("Preview"));
		expect(screen.getByText("Required field")).toBeTruthy();
	});
});
