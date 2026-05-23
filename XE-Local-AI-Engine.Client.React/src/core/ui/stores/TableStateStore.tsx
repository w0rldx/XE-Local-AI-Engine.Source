import { create } from "zustand";
import { devtools, persist } from "zustand/middleware";

import type { ITableStateStore } from "@/core/ui/models/TableStateModels";

export const createTableStateStore = (key: string) =>
	create<ITableStateStore>()(
		devtools(
			persist(
				(set) => ({
					columnFilters: undefined,
					setColumnFilters: (columnFilter) =>
						set(() => ({
							columnFilters: columnFilter,
						})),
					sorting: undefined,
					setSorting: (sorting) =>
						set(() => ({
							sorting,
						})),
					globalFilter: "",
					setGlobalFilter: (value) =>
						set(() => ({
							globalFilter: value,
						})),
					columnVisibility: {},
					setColumnVisibility: (columnVisibility) =>
						set(() => ({
							columnVisibility,
						})),
					pagination: undefined,
					setPagination: (pagination) =>
						set(() => ({
							pagination,
						})),
					density: undefined,
					setDensity: (density) =>
						set(() => ({
							density,
						})),
				}),
				{
					name: key,
				},
			),
		),
	);
