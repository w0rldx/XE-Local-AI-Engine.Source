export interface TableFilterPagedResult<T> {
	page: number;
	pageSize: number;
	totalCount: number;
	totalPages: number;
	items: T;
}
