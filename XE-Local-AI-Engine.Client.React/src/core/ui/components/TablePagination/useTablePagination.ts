import { useCallback, useEffect, useMemo, useState } from "react";

import type { TablePaginationFooterProps } from "@/core/ui/components/TablePagination/TablePaginationFooter";
import { useTablePaginationStore } from "@/core/ui/components/TablePagination/useTablePaginationStore";

// Default rows-per-page and the selectable sizes offered by the footer. Kept here so every table that opts into
// pagination shares one set of defaults; callers override per-table via UseTablePaginationOptions.
export const DEFAULT_PAGE_SIZE = 25;
const DEFAULT_PAGE_SIZE_OPTIONS: readonly number[] = [10, 25, 50, 100];

/** The page a server-paged table is on and its page size, persisted under `storageKey` like the client-side twin's. */
export function useServerPageState(storageKey: string): Omit<ServerTablePaginationInput, "totalItems"> {
	return usePageState({ storageKey });
}

// The requested page and page size both pagination hooks own. The size comes from the persisted store when a
// storageKey is supplied (so it survives a reload), otherwise from local state; both selectors are read
// unconditionally to satisfy the rules of hooks.
function usePageState(options: UseTablePaginationOptions): Omit<ServerTablePaginationInput, "totalItems"> {
	const { storageKey } = options;
	const pageSizeOptions = options.pageSizeOptions ?? DEFAULT_PAGE_SIZE_OPTIONS;
	const fallbackPageSize = options.initialPageSize ?? DEFAULT_PAGE_SIZE;
	const persistedPageSize = useTablePaginationStore((state) =>
		storageKey === undefined ? undefined : state.pageSizeByKey[storageKey],
	);
	const persistSetPageSize = useTablePaginationStore((state) => state.setPageSize);
	const [localPageSize, setLocalPageSize] = useState(fallbackPageSize);
	const [page, setPage] = useState(1);

	// Changing the page size resets to the first page: the old page number means nothing against the new size.
	const onPageSizeChange = useCallback(
		(next: number) => {
			if (storageKey === undefined) {
				setLocalPageSize(next);
			} else {
				persistSetPageSize(storageKey, next);
			}
			setPage(1);
		},
		[storageKey, persistSetPageSize],
	);

	return {
		page,
		// Storage is untrusted: a 0 emptied the table, a string made `start + pageSize` concatenate, and a size no
		// longer offered left the selector blank. Only a size the selector offers is used.
		pageSize:
			storageKey === undefined
				? localPageSize
				: persistedPageSize !== undefined && pageSizeOptions.includes(persistedPageSize)
					? persistedPageSize
					: fallbackPageSize,
		pageSizeOptions,
		onPageChange: setPage,
		onPageSizeChange,
	};
}

/**
 * `placeholderData` for a server-paged list query: while the next page loads, hold the previous page of the SAME list
 * (only `limit`/`offset` changed) so the pager keeps its total, but never another filter's rows.
 */
export function keepPreviousPageOf(query: object) {
	const list = listIdentity(query);
	return <T>(previous: T | undefined, previousQuery?: { readonly queryKey: readonly unknown[] }): T | undefined =>
		listIdentity((previousQuery?.queryKey[0] as { query?: object } | undefined)?.query) === list ? previous : undefined;
}

// The list a query reads, whatever page: its parameters with the paging pair blanked.
function listIdentity(query: object | undefined): string {
	return JSON.stringify({ ...query, limit: 0, offset: 0 });
}

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
	const {
		page: requestedPage,
		pageSize,
		pageSizeOptions,
		onPageChange: setRequestedPage,
		onPageSizeChange: setPageSize,
	} = usePageState(options);

	const totalItems = items.length;
	const pageCount = Math.max(1, Math.ceil(totalItems / pageSize));
	const page = Math.min(Math.max(1, requestedPage), pageCount);

	const pageItems = useMemo(() => {
		const start = (page - 1) * pageSize;
		return items.slice(start, start + pageSize);
	}, [items, page, pageSize]);

	const firstItemIndex = totalItems === 0 ? 0 : (page - 1) * pageSize + 1;
	const lastItemIndex = totalItems === 0 ? 0 : firstItemIndex + pageItems.length - 1;

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
	// The clamp lands on the NEXT render, so every number here is computed from the page that will actually be shown.
	// Reading the raw page instead rendered "201–60 of 60" for the frame in between.
	const activePage = Math.min(Math.max(1, page), pageCount);

	useEffect(() => {
		if (page > pageCount) {
			onPageChange(pageCount);
		}
	}, [onPageChange, page, pageCount]);

	return {
		page: activePage,
		pageCount,
		pageSize,
		totalItems,
		firstItemIndex: totalItems === 0 ? 0 : (activePage - 1) * pageSize + 1,
		lastItemIndex: Math.min(activePage * pageSize, totalItems),
		pageSizeOptions,
		onPageChange,
		onPageSizeChange,
	};
}
