import type { ColumnFiltersState, PaginationState, SortingState, VisibilityState } from "@tanstack/react-table";

export type DensityState = "xs" | "md" | "lg";

export interface ITableStateStore {
	columnFilters: ColumnFiltersState | undefined;
	setColumnFilters: (columnFilter: ColumnFiltersState) => void;
	sorting: SortingState | undefined;
	setSorting: (sorting: SortingState) => void;
	globalFilter: string;
	setGlobalFilter: (value: string) => void;
	columnVisibility: VisibilityState | undefined;
	setColumnVisibility: (columnVisibility: VisibilityState) => void;
	pagination: PaginationState | undefined;
	setPagination: (pagination: PaginationState) => void;
	density: DensityState | undefined;
	setDensity: (density: DensityState) => void;
}
