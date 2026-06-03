// @vitest-environment jsdom

import "@/i18n";

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { TablePaginationFooter } from "@/core/ui/components/TablePagination/TablePaginationFooter";

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

const baseProps = {
	page: 1,
	pageCount: 3,
	pageSize: 25,
	totalItems: 60,
	firstItemIndex: 1,
	lastItemIndex: 25,
	pageSizeOptions: [10, 25, 50, 100],
	onPageChange: vi.fn(),
	onPageSizeChange: vi.fn(),
};

describe("TablePaginationFooter", () => {
	beforeEach(() => {
		// jsdom does not implement scrollIntoView; Mantine's Combobox calls it when the size-select dropdown opens.
		Element.prototype.scrollIntoView = vi.fn();
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
		vi.clearAllMocks();
	});

	it("renders the visible row range", () => {
		renderWithProviders(<TablePaginationFooter {...baseProps} />);

		const range = screen.getByTestId("table-pagination-range").textContent ?? "";
		expect(range).toContain("1");
		expect(range).toContain("25");
		expect(range).toContain("60");
	});

	it("emits onPageChange when a page control is clicked", () => {
		const onPageChange = vi.fn();
		renderWithProviders(<TablePaginationFooter {...baseProps} onPageChange={onPageChange} />);

		fireEvent.click(screen.getByText("2"));

		expect(onPageChange).toHaveBeenCalledWith(2);
	});

	it("emits onPageSizeChange when a new size is picked", async () => {
		const onPageSizeChange = vi.fn();
		renderWithProviders(<TablePaginationFooter {...baseProps} onPageSizeChange={onPageSizeChange} />);

		// Mantine Select forwards data-testid onto the inner input element; click it to open the dropdown.
		fireEvent.click(screen.getByTestId("table-pagination-size"));
		fireEvent.click(await screen.findByText("50"));

		expect(onPageSizeChange).toHaveBeenCalledWith(50);
	});

	it("hides the page navigator when there is a single page", () => {
		renderWithProviders(<TablePaginationFooter {...baseProps} pageCount={1} totalItems={8} lastItemIndex={8} />);

		expect(screen.queryByTestId("table-pagination-controls")).toBeNull();
		// The size selector and range label remain available on a single-page table.
		expect(screen.getByTestId("table-pagination-size")).toBeTruthy();
		expect(screen.getByTestId("table-pagination-range")).toBeTruthy();
	});

	it("uses a custom data-testid prefix", () => {
		renderWithProviders(<TablePaginationFooter {...baseProps} data-testid="runs-pager" />);

		expect(screen.getByTestId("runs-pager")).toBeTruthy();
		expect(screen.getByTestId("runs-pager-range")).toBeTruthy();
		expect(screen.getByTestId("runs-pager-controls")).toBeTruthy();
	});
});
