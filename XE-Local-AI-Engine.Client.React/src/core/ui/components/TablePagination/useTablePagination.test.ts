// @vitest-environment jsdom

import { act, renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import {
	DEFAULT_PAGE_SIZE,
	useServerTablePagination,
	useTablePagination,
} from "@/core/ui/components/TablePagination/useTablePagination";
import { useTablePaginationStore } from "@/core/ui/components/TablePagination/useTablePaginationStore";

const range = (count: number): number[] => Array.from({ length: count }, (_, index) => index);

describe("useTablePagination", () => {
	afterEach(() => {
		// Reset the persisted store so a storageKey test never leaks its page size into a later test.
		useTablePaginationStore.setState({ pageSizeByKey: {} });
		localStorage.clear();
	});
	it("slices the first page using the default page size", () => {
		const items = range(60);
		const { result } = renderHook(() => useTablePagination(items));

		expect(result.current.pageSize).toBe(DEFAULT_PAGE_SIZE);
		expect(result.current.page).toBe(1);
		expect(result.current.pageCount).toBe(3);
		expect(result.current.totalItems).toBe(60);
		expect(result.current.pageItems).toHaveLength(25);
		expect(result.current.pageItems[0]).toBe(0);
		expect(result.current.firstItemIndex).toBe(1);
		expect(result.current.lastItemIndex).toBe(25);
	});

	it("slices the requested page and reports a partial last page", () => {
		const items = range(60);
		const { result } = renderHook(() => useTablePagination(items));

		act(() => result.current.setPage(3));

		expect(result.current.page).toBe(3);
		expect(result.current.pageItems).toHaveLength(10);
		expect(result.current.pageItems[0]).toBe(50);
		expect(result.current.firstItemIndex).toBe(51);
		expect(result.current.lastItemIndex).toBe(60);
	});

	it("resets to the first page when the page size changes", () => {
		const items = range(60);
		const { result } = renderHook(() => useTablePagination(items));

		act(() => result.current.setPage(3));
		act(() => result.current.setPageSize(10));

		expect(result.current.page).toBe(1);
		expect(result.current.pageSize).toBe(10);
		expect(result.current.pageCount).toBe(6);
		expect(result.current.pageItems).toHaveLength(10);
	});

	it("clamps the active page when the list shrinks below it", () => {
		const { result, rerender } = renderHook(({ items }) => useTablePagination(items), {
			initialProps: { items: range(60) },
		});

		act(() => result.current.setPage(3));
		expect(result.current.page).toBe(3);

		// A filter narrows the list to a single page: the clamped page never strands the caller on an empty slice.
		rerender({ items: range(5) });

		expect(result.current.page).toBe(1);
		expect(result.current.pageCount).toBe(1);
		expect(result.current.pageItems).toHaveLength(5);
	});

	it("treats an empty list as a single empty page with zero-based indices", () => {
		const { result } = renderHook(() => useTablePagination<number>([]));

		expect(result.current.pageCount).toBe(1);
		expect(result.current.totalItems).toBe(0);
		expect(result.current.pageItems).toHaveLength(0);
		expect(result.current.firstItemIndex).toBe(0);
		expect(result.current.lastItemIndex).toBe(0);
	});

	it("honours custom initial page size and options", () => {
		const items = range(30);
		const { result } = renderHook(() => useTablePagination(items, { initialPageSize: 10, pageSizeOptions: [10, 20] }));

		expect(result.current.pageSize).toBe(10);
		expect(result.current.pageCount).toBe(3);
		expect(result.current.pageSizeOptions).toEqual([10, 20]);
	});

	it("persists the selected page size under a storageKey across hook remounts", () => {
		const items = range(60);

		const first = renderHook(() => useTablePagination(items, { storageKey: "runs" }));
		act(() => first.result.current.setPageSize(50));
		expect(first.result.current.pageSize).toBe(50);

		// A fresh hook (mimicking a browser reload) with the same storageKey reads the persisted size.
		first.unmount();
		const second = renderHook(() => useTablePagination(items, { storageKey: "runs" }));

		expect(second.result.current.pageSize).toBe(50);
		// The active page is intentionally NOT persisted — a reload starts at page 1.
		expect(second.result.current.page).toBe(1);
	});

	it("keeps page sizes isolated per storageKey", () => {
		const items = range(60);

		const runs = renderHook(() => useTablePagination(items, { storageKey: "runs" }));
		const models = renderHook(() => useTablePagination(items, { storageKey: "models" }));

		act(() => runs.result.current.setPageSize(10));

		expect(runs.result.current.pageSize).toBe(10);
		expect(models.result.current.pageSize).toBe(DEFAULT_PAGE_SIZE);
	});
});

describe("useServerTablePagination", () => {
	const pageSizeOptions = [25, 50, 100];

	function renderServerPagination(page: number, totalItems: number) {
		const onPageChange = vi.fn();
		const onPageSizeChange = vi.fn();
		const { result } = renderHook(() =>
			useServerTablePagination({ page, pageSize: 50, totalItems, pageSizeOptions, onPageChange, onPageSizeChange }),
		);
		return { footer: result.current, onPageChange };
	}

	it("describes the rows the active page holds", () => {
		const { footer, onPageChange } = renderServerPagination(2, 130);

		expect(footer.page).toBe(2);
		expect(footer.pageCount).toBe(3);
		expect(footer.firstItemIndex).toBe(51);
		expect(footer.lastItemIndex).toBe(100);
		expect(onPageChange).not.toHaveBeenCalled();
	});

	// A total that shrank under the operator (a delete, not a filter change) leaves the requested page out of range.
	// The range must describe the page that will actually be shown, not the one that no longer exists — reading the
	// raw page rendered "201–60 of 60" for a frame.
	it("clamps to the last page that still exists and states its range, not the requested page's", () => {
		const { footer, onPageChange } = renderServerPagination(5, 60);

		expect(footer.page).toBe(2);
		expect(footer.pageCount).toBe(2);
		expect(footer.firstItemIndex).toBe(51);
		expect(footer.lastItemIndex).toBe(60);
		expect(onPageChange).toHaveBeenCalledExactlyOnceWith(2);
	});

	it("reports an empty table as an empty range rather than 1-0", () => {
		const { footer, onPageChange } = renderServerPagination(1, 0);

		expect(footer.page).toBe(1);
		expect(footer.pageCount).toBe(1);
		expect(footer.firstItemIndex).toBe(0);
		expect(footer.lastItemIndex).toBe(0);
		expect(onPageChange).not.toHaveBeenCalled();
	});
});
