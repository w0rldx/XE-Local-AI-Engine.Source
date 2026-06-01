// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ExpandableTextField } from "@/core/ui/components/ExpandableTextField/ExpandableTextField";

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

describe("ExpandableTextField", () => {
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

	it("renders the clamped preview with its label and is collapsed by default", () => {
		renderWithProviders(<ExpandableTextField label="License" value="Some short license" />);

		expect(screen.getByText(/License: Some short license/)).toBeTruthy();
		// No dialog — inline toggle only
		expect(screen.queryByRole("dialog")).toBeNull();
		// Toggle starts collapsed: aria-expanded=false and shows the expand label
		const toggle = screen.getByRole("button", { name: /show full/i });
		expect(toggle.getAttribute("aria-expanded")).toBe("false");
	});

	it("expands inline when the toggle is clicked and collapses again on second click", () => {
		renderWithProviders(<ExpandableTextField label="License" value="full license body text" />);

		const toggle = screen.getByRole("button", { name: /show full/i });
		expect(toggle.getAttribute("aria-expanded")).toBe("false");

		fireEvent.click(toggle);
		// After expanding: aria-expanded=true, label changes to "Show less"
		expect(toggle.getAttribute("aria-expanded")).toBe("true");
		expect(screen.getByRole("button", { name: /show less/i })).toBeTruthy();

		fireEvent.click(toggle);
		// After collapsing: aria-expanded=false, label back to "Show full"
		expect(toggle.getAttribute("aria-expanded")).toBe("false");
		expect(screen.getByRole("button", { name: /show full/i })).toBeTruthy();
	});

	it("never renders a dialog", () => {
		const fullValue = "line one\nline two\nfull license body text";
		renderWithProviders(<ExpandableTextField label="License" value={fullValue} />);

		fireEvent.click(screen.getByRole("button", { name: /show full/i }));

		expect(screen.queryByRole("dialog")).toBeNull();
	});

	it("honors a custom expand label", () => {
		renderWithProviders(<ExpandableTextField label="Notes" value="x" expandLabel="Expand notes" />);

		expect(screen.getByText("Expand notes")).toBeTruthy();
	});

	it("honors a custom collapse label after expanding", () => {
		renderWithProviders(<ExpandableTextField label="Notes" value="x" expandLabel="Expand notes" collapseLabel="Collapse notes" />);

		fireEvent.click(screen.getByRole("button", { name: /expand notes/i }));
		expect(screen.getByRole("button", { name: /collapse notes/i })).toBeTruthy();
	});
});
