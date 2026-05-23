import type { ColumnFiltersState, PaginationState, SortingState, VisibilityState } from "@tanstack/react-table";
import { useState } from "react";

import type { DensityState } from "@/core/ui/models/TableStateModels";
import { createTableStateStore } from "@/core/ui/stores/TableStateStore";

const tableStateStoreCache = new Map<string, ReturnType<typeof createTableStateStore>>();

const getTableStateStore = (key: string) => {
	const cachedStore = tableStateStoreCache.get(key);

	if (cachedStore) {
		return cachedStore;
	}

	const tableStateStore = createTableStateStore(key);
	tableStateStoreCache.set(key, tableStateStore);

	return tableStateStore;
};

export default function useTableState(key: string, defaultSorting: SortingState = [], disabledColumnFilterStates: string[] = []) {
	const useTableStore = getTableStateStore(key);

	const {
		columnFilters: tableColumnFilters,
		globalFilter: tableGlobalFilter,
		sorting: tableSorting,
		columnVisibility: tableColumnVisibility,
		pagination: tablePagination,
		density: tableDensity,
		setColumnFilters: setTableColumnFilters,
		setGlobalFilter: setTableGlobalFilter,
		setSorting: setTableSorting,
		setColumnVisibility: setTableColumnVisibility,
		setPagination: setTablePagination,
		setDensity: setTableDensity,
	} = useTableStore();

	const [columnFilters, setColumnFilters] = useState<ColumnFiltersState>(tableColumnFilters ?? []);
	const [globalFilter, setGlobalFilter] = useState<string>(tableGlobalFilter ?? "");
	const [sorting, setSorting] = useState<SortingState>(tableSorting ?? defaultSorting);
	const [columnVisibility, setColumnVisibility] = useState<VisibilityState>(tableColumnVisibility ?? {});
	const [pagination, setPagination] = useState<PaginationState>({
		pageIndex: 0,
		pageSize: tablePagination?.pageSize ?? 20,
	});
	const [density, setDensity] = useState<DensityState>(tableDensity ?? "md");

	const setColumnFiltersState = (columnFiltersStates: ColumnFiltersState) => {
		setColumnFilters(columnFiltersStates);

		if (disabledColumnFilterStates.length > 0) {
			const filteredColumnFilters = columnFiltersStates.filter(
				(columnFilter) => !disabledColumnFilterStates.includes(columnFilter.id),
			);
			setTableColumnFilters(filteredColumnFilters);
		} else {
			setTableColumnFilters(columnFiltersStates);
		}
	};

	const setGlobalFilterState = (globalFilterStates: string) => {
		setGlobalFilter(globalFilterStates);
		setTableGlobalFilter(globalFilterStates);
	};

	const setSortingState = (sortingState: SortingState) => {
		setSorting(sortingState);
		setTableSorting(sortingState);
	};

	const setColumnVisibilityState = (columnVisibilityState: Record<string, boolean>) => {
		setColumnVisibility(columnVisibilityState);
		setTableColumnVisibility(columnVisibilityState);
	};

	const setPaginationState = (paginationState: PaginationState) => {
		setPagination(paginationState);
		setTablePagination(paginationState);
	};

	const setDensityState = (rowDensityState: DensityState) => {
		setDensity(rowDensityState);
		setTableDensity(rowDensityState);
	};

	return {
		columnFilters,
		globalFilter,
		sorting,
		columnVisibility,
		pagination,
		density,
		setColumnFilters: setColumnFiltersState,
		setGlobalFilter: setGlobalFilterState,
		setSorting: setSortingState,
		setColumnVisibility: setColumnVisibilityState,
		setPagination: setPaginationState,
		setDensity: setDensityState,
	};
}
