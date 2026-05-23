interface SortingParameter {
	id: string;
	desc: boolean;
}

interface ColumnFilter {
	id: string;
	value: unknown;
}

export interface TableQueryParameters {
	page: number;
	pageSize: number;
	search?: string;
	sorting?: SortingParameter[];
	columnFilters?: ColumnFilter[];
}
