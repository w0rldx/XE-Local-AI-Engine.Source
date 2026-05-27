// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
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

	it("renders the clamped preview with its label", () => {
		renderWithProviders(<ExpandableTextField label="License" value="Some short license" />);

		expect(screen.getByText(/License: Some short license/)).toBeTruthy();
		expect(screen.queryByRole("dialog")).toBeNull();
	});

	it("opens a dialog with the full value when expanded", async () => {
		const fullValue = "line one\nline two\nfull license body text";
		renderWithProviders(<ExpandableTextField label="License" value={fullValue} dialogTitle="Model license" />);

		fireEvent.click(screen.getByText("Show full"));

		const dialog = await screen.findByRole("dialog");
		expect(within(dialog).getByText("Model license")).toBeTruthy();
		expect(within(dialog).getByText(/full license body text/)).toBeTruthy();
	});

	it("honors a custom expand label", () => {
		renderWithProviders(<ExpandableTextField label="Notes" value="x" expandLabel="Expand notes" />);

		expect(screen.getByText("Expand notes")).toBeTruthy();
	});
});
