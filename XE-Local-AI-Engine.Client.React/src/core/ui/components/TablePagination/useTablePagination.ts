import { useCallback, useEffect, useMemo, useState } from "react";

import type { TablePaginationFooterProps } from "@/core/ui/components/TablePagination/TablePaginationFooter";
import { useTablePaginationStore } from "@/core/ui/components/TablePagination/useTablePaginationStore";

// Default rows-per-page and the selectable sizes offered by the footer. Kept here so every table that opts into
// pagination shares one set of defaults; callers override per-table via UseTablePaginationOptions.
export const DEFAULT_PAGE_SIZE = 25;
const DEFAULT_PAGE_SIZE_OPTIONS: readonly number[] = [10, 25, 50, 100];

export interface UseTablePaginationOptions {
	/** Rows per page on first render. Defaults to {@link DEFAULT_PAGE_SIZE}. */
	readonly initialPageSize?: number;
	/** Sizes offered by the page-size selector. Defaults to {@link DEFAULT_PAGE_SIZE_OPTIONS}. */
	readonly pageSizeOptions?: readonly number[];
	/**
	 * When set, the last-selected page size is persisted across browser reloads (localStorage), keyed by this
	 * string so each table remembers its own preference. The active page is NOT persisted — a reload starts at
	 * page 1. Omit for in-memory-only pagination.
	 */
	readonly storageKey?: string;
}

export interface TablePagination<T> {
	/** 1-based active page, always clamped into [1, pageCount]. */
	readonly page: number;
	readonly pageSize: number;
	/** Total number of pages, never below 1 (an empty list still has one — empty — page). */
	readonly pageCount: number;
	readonly totalItems: number;
	/** The slice of items for the active page. */
	readonly pageItems: readonly T[];
	/** 1-based index of the first visible row, or 0 when the list is empty. */
	readonly firstItemIndex: number;
	/** 1-based index of the last visible row, or 0 when the list is empty. */
	readonly lastItemIndex: number;
	readonly pageSizeOptions: readonly number[];
	readonly setPage: (page: number) => void;
	readonly setPageSize: (pageSize: number) => void;
}

// Client-side pagination state for an already-loaded list. The list is the source of truth: callers pass the full
// (filtered) array and render `pageItems`. The active page is derived-clamped on every render rather than reset via
// an effect, so the page stays valid when the list shrinks (e.g. a filter narrows it) WITHOUT resetting on every
// data refresh — important for lists behind a polling query whose array identity changes on each refetch.
export function useTablePagination<T>(items: readonly T[], options: UseTablePaginationOptions = {}): TablePagination<T> {
	const { storageKey } = options;
	const pageSizeOptions = options.pageSizeOptions ?? DEFAULT_PAGE_SIZE_OPTIONS;
	const fallbackPageSize = options.initialPageSize ?? DEFAULT_PAGE_SIZE;

	// Page size is sourced from the persisted store when a storageKey is supplied (so it survives a reload),
	// otherwise from local component state. Both selectors are read unconditionally to satisfy the rules of
	// hooks; only one is authoritative per render.
	const persistedPageSize = useTablePaginationStore((state) =>
		storageKey === undefined ? undefined : state.pageSizeByKey[storageKey],
	);
	const persistSetPageSize = useTablePaginationStore((state) => state.setPageSize);
	const [localPageSize, setLocalPageSize] = useState(fallbackPageSize);

	const [requestedPage, setRequestedPage] = useState(1);

	const pageSize = storageKey === undefined ? localPageSize : (persistedPageSize ?? fallbackPageSize);

	const totalItems = items.length;
	const pageCount = Math.max(1, Math.ceil(totalItems / pageSize));
	const page = Math.min(Math.max(1, requestedPage), pageCount);

	const pageItems = useMemo(() => {
		const start = (page - 1) * pageSize;
		return items.slice(start, start + pageSize);
	}, [items, page, pageSize]);

	const firstItemIndex = totalItems === 0 ? 0 : (page - 1) * pageSize + 1;
	const lastItemIndex = totalItems === 0 ? 0 : firstItemIndex + pageItems.length - 1;

	// Changing the page size resets to the first page so the operator always lands on a predictable position
	// rather than an arbitrary offset computed from the previous size. The new size is persisted when a
	// storageKey is supplied, otherwise it stays in local state.
	const setPageSize = useCallback(
		(next: number) => {
			if (storageKey === undefined) {
				setLocalPageSize(next);
			} else {
				persistSetPageSize(storageKey, next);
			}
			setRequestedPage(1);
		},
		[storageKey, persistSetPageSize],
	);

	return {
		page,
		pageSize,
		pageCount,
		totalItems,
		pageItems,
		firstItemIndex,
		lastItemIndex,
		pageSizeOptions,
		setPage: setRequestedPage,
		setPageSize,
	};
}

/** The page state a server-paged table owns, plus the server's total. The hook derives the footer's props from it. */
export interface ServerTablePaginationInput {
	readonly page: number;
	readonly pageSize: number;
	/** Rows the CURRENT filters match on the server, not the length of the page on screen. */
	readonly totalItems: number;
	readonly pageSizeOptions: readonly number[];
	readonly onPageChange: (page: number) => void;
	readonly onPageSizeChange: (pageSize: number) => void;
}

// Footer props for a table the SERVER pages: the caller sends `limit`/`offset` with its query and passes the
// response's total back in. The row math is the same as the client-side twin above, but the page cannot be
// derive-clamped here — the offset that fetched the current rows was already sent — so a total that shrank below the
// active page (a delete, not a filter change: those reset to page 1 at the call site) is corrected by asking for the
// last page that still exists, which re-issues the query rather than leaving the operator on an empty page.
export function useServerTablePagination({
	page,
	pageSize,
	totalItems,
	pageSizeOptions,
	onPageChange,
	onPageSizeChange,
}: ServerTablePaginationInput): TablePaginationFooterProps {
	const pageCount = Math.max(1, Math.ceil(totalItems / pageSize));

	useEffect(() => {
		if (page > pageCount) {
			onPageChange(pageCount);
		}
	}, [onPageChange, page, pageCount]);

	return {
		page: Math.min(Math.max(1, page), pageCount),
		pageCount,
		pageSize,
		totalItems,
		firstItemIndex: totalItems === 0 ? 0 : (page - 1) * pageSize + 1,
		lastItemIndex: Math.min(page * pageSize, totalItems),
		pageSizeOptions,
		onPageChange,
		onPageSizeChange,
	};
}
