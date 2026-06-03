// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { MarkdownView } from "@/core/ui/components/MarkdownView/MarkdownView";

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

describe("MarkdownView", () => {
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
	});

	afterEach(() => {
		cleanup();
	});

	it("renders plain paragraph text", () => {
		renderWithProviders(<MarkdownView content="Hello world" />);
		expect(screen.getByText("Hello world")).toBeTruthy();
	});

	it("renders a heading via remark-gfm / commonmark", () => {
		renderWithProviders(<MarkdownView content="# My Heading" />);
		expect(screen.getByRole("heading", { level: 1 })).toBeTruthy();
		expect(screen.getByText("My Heading")).toBeTruthy();
	});

	it("renders an unordered list item", () => {
		// biome-ignore lint/style/useConsistentCurlyBraces: \n must be a real newline — JSX string attribute would treat it as literal backslash-n
		renderWithProviders(<MarkdownView content={"- item one\n- item two"} />);
		expect(screen.getByText("item one")).toBeTruthy();
		expect(screen.getByText("item two")).toBeTruthy();
	});

	it("renders inline code without a language label", () => {
		renderWithProviders(<MarkdownView content="Use `const x = 1`" />);
		expect(screen.getByText("const x = 1")).toBeTruthy();
	});

	it("renders a fenced code block with a language", () => {
		// biome-ignore lint/style/useConsistentCurlyBraces: \n must be a real newline — JSX string attribute would treat it as literal backslash-n
		renderWithProviders(<MarkdownView content={'```json\n{"key": "value"}\n```'} />);
		// SyntaxHighlighter wraps the code — at minimum the raw text should be present
		expect(screen.getByText(/"key"/)).toBeTruthy();
	});

	it("renders strikethrough via remark-gfm", () => {
		renderWithProviders(<MarkdownView content="~~deleted~~" />);
		// remark-gfm wraps in <del>; text content should still be accessible
		expect(screen.getByText("deleted")).toBeTruthy();
	});
});
