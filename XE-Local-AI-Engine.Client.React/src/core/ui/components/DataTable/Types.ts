import type {
	ColumnDef,
	ColumnFiltersState,
	PaginationState,
	Row,
	RowData,
	SortingState,
	Table,
	TableOptions,
	VisibilityState,
} from "@tanstack/react-table";
import type { ComponentType, MutableRefObject, ReactNode } from "react";

import type { TableFilterPagedResult } from "@/core/ui/models/TableFilterResult";

type DataTableRowData = RowData;
export type DataTableColumnFiltersState = ColumnFiltersState;
export type DataTableSortingState = SortingState;
export type DataTableVisibilityState = VisibilityState;
export type DataTablePaginationState = PaginationState;
export type DataTableDensityState = "xs" | "md" | "lg";
export type DataTableRow<TData extends DataTableRowData> = Row<TData>;
export type DataTableColumnDef<TData extends DataTableRowData> = ColumnDef<TData>;
type DataTableQueryMode = "simple" | "paginated";

export type DataTableTableInstance<TData extends DataTableRowData> = Table<TData> & {
	setEditingRow: (row: DataTableRow<TData> | null) => void;
	setCreatingRow: (creatingRow: boolean | null) => void;
};

interface IDataTableRowAction<TData extends DataTableRowData> {
	icon: ReactNode;
	tooltip: string;
	onClick: (row: DataTableRow<TData>) => void | Promise<void>;
	condition?: (row: DataTableRow<TData>) => boolean;
}

interface IDataTableActionState<TData extends DataTableRowData> {
	disabled: boolean;
	tooltip?: string;
	condition?: (row: DataTableRow<TData>) => boolean;
}

interface IDataTableActionCapabilities {
	create?: boolean;
	edit?: boolean;
	delete?: boolean;
	view?: boolean;
}

interface IDataTableDisplayOptions {
	rowNumbers?: boolean;
	showRowActionsInColumnToggle?: boolean;
}

export type DataTableEntityRow = DataTableRowData & {
	id: string | number;
};

export type PaginatedQueryFunction<TData> = (
	page: number,
	pageSize: number,
	globalFilter: string,
	sorting: DataTableSortingState,
	columnFilters: DataTableColumnFiltersState,
) => Promise<TableFilterPagedResult<TData[]>>;

export type SimpleQueryFunction<TData> = () => Promise<TData[]>;

export interface IDataTableProperties<TData extends DataTableEntityRow> {
	tableKey: string;
	title: string;
	subtitle?: string;
	columns: DataTableColumnDef<TData>[];
	queryKey: string[];
	queryFn: PaginatedQueryFunction<TData> | SimpleQueryFunction<TData>;
	queryMode?: DataTableQueryMode;
	actionCapabilities?: IDataTableActionCapabilities;
	createButtonText?: string;
	onCreateClick?: () => void;
	editTooltip?: string;
	deleteTooltip?: string;
	viewTooltip?: string;
	getEditActionState?: (row: DataTableRow<TData>) => IDataTableActionState<TData> | undefined;
	getDeleteActionState?: (row: DataTableRow<TData>) => IDataTableActionState<TData> | undefined;
	getViewActionState?: (row: DataTableRow<TData>) => IDataTableActionState<TData> | undefined;
	createModalComponent?: ComponentType<{ table: DataTableTableInstance<TData> }>;
	editModalComponent?: ComponentType<{ row: DataTableRow<TData>; table: DataTableTableInstance<TData> }>;
	viewModalComponent?: ComponentType<{ row: DataTableRow<TData>; table: DataTableTableInstance<TData> }>;
	deleteMutation?: {
		mutateAsync: (id: string, options?: { onSuccess?: () => void }) => Promise<void>;
	};
	deleteDialogTitle?: string;
	deleteDialogText?: string;
	deleteSuccessMessage?: string;
	customRowActions?: IDataTableRowAction<TData>[];
	displayOptions?: IDataTableDisplayOptions;
	maxHeight?: string;
	additionalToolbarActions?: (table: DataTableTableInstance<TData>) => ReactNode;
	tableRef?: MutableRefObject<DataTableTableInstance<TData> | null>;
	tableOptions?: Partial<Pick<TableOptions<TData>, "enableSorting" | "enableColumnFilters" | "enableHiding">>;
}
