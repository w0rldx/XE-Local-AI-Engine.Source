import { create } from "zustand";
import { persist } from "zustand/middleware";

// Persisted rows-per-page preference for paginated tables. Only the last-selected page size survives a browser
// reload (the active page intentionally does not — a reload starts at page 1). Sizes are keyed by the caller's
// storageKey so each table remembers its own preference independently. The active page itself stays transient
// in useTablePagination's local state. Backed by localStorage via the persist middleware.
interface TablePaginationStoreState {
	readonly pageSizeByKey: Readonly<Record<string, number>>;
	readonly setPageSize: (key: string, pageSize: number) => void;
}

export const useTablePaginationStore = create<TablePaginationStoreState>()(
	persist(
		(set) => ({
			pageSizeByKey: {},
			setPageSize: (key, pageSize) =>
				set((state) => ({ pageSizeByKey: { ...state.pageSizeByKey, [key]: pageSize } })),
		}),
		{ name: "xe-table-pagination" },
	),
);
