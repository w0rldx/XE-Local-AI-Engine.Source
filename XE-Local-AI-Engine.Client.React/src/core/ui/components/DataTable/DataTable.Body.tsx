import { Group, Loader, ScrollArea, Table, Text, TextInput } from "@mantine/core";
import { IconSortAscending, IconSortDescending } from "@tabler/icons-react";
import { type ColumnDef, flexRender } from "@tanstack/react-table";
import { m } from "framer-motion";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";

import type { DataTableColumnFiltersState, DataTableEntityRow, DataTableTableInstance } from "@/core/ui/components/DataTable/Types";

interface DataTableBodyProps<TData extends DataTableEntityRow> {
	tableInstance: DataTableTableInstance<TData>;
	resolvedColumns: ColumnDef<TData>[];
	columnFilters: DataTableColumnFiltersState;
	showColumnFilters: boolean;
	isLoading: boolean;
	densityPadding: string;
	tableSpacing: "xs" | "sm" | "md";
	headerBackground: string;
	tableBorderColor: string;
	maxHeight?: string;
	defaultMaxHeight: string;
}

export function DataTableBody<TData extends DataTableEntityRow>({
	tableInstance,
	resolvedColumns,
	columnFilters,
	showColumnFilters,
	isLoading,
	densityPadding,
	tableSpacing,
	headerBackground,
	tableBorderColor,
	maxHeight,
	defaultMaxHeight,
}: DataTableBodyProps<TData>) {
	const { t } = useTranslation();

	return (
		<ScrollArea h={maxHeight || defaultMaxHeight}>
			<Table
				stickyHeader={true}
				withTableBorder={false}
				withColumnBorders={false}
				withRowBorders={true}
				highlightOnHover={true}
				striped={false}
				verticalSpacing={tableSpacing}
				horizontalSpacing={tableSpacing}
			>
				<Table.Thead>
					{tableInstance.getHeaderGroups().map((headerGroup) => (
						<Table.Tr key={headerGroup.id}>
							{headerGroup.headers.map((header) => {
								const sorted = header.column.getIsSorted();
								const canSort = header.column.getCanSort();
								const sortIcon: ReactNode =
									sorted === "asc" ? (
										<IconSortAscending size={16} />
									) : sorted === "desc" ? (
										<IconSortDescending size={16} />
									) : null;

								return (
									<Table.Th
										key={header.id}
										style={{
											paddingBlock: densityPadding,
											backgroundColor: headerBackground,
											borderBottom: `1px solid ${tableBorderColor}`,
											fontWeight: 600,
										}}
									>
										{header.isPlaceholder ? null : (
											<m.div
												onClick={canSort ? header.column.getToggleSortingHandler() : undefined}
												whileHover={canSort ? { scale: 1.01 } : undefined}
												whileTap={canSort ? { scale: 0.99 } : undefined}
												transition={{ duration: 0.12, ease: "easeOut" }}
												style={{
													cursor: canSort ? "pointer" : "default",
													display: "flex",
													alignItems: "center",
													justifyContent: "space-between",
													gap: "0.25rem",
												}}
											>
												{flexRender(header.column.columnDef.header, header.getContext())}
												{sortIcon}
											</m.div>
										)}
									</Table.Th>
								);
							})}
						</Table.Tr>
					))}
					{showColumnFilters && (
						<Table.Tr>
							{tableInstance.getFlatHeaders().map((header) => {
								const column = header.column;
								const existingFilter = columnFilters.find((f) => f.id === column.id);
								const currentValue = typeof existingFilter?.value === "string" ? existingFilter.value : "";

								return (
									<Table.Th
										key={`filter-${header.id}`}
										style={{
											paddingBlock: densityPadding,
											backgroundColor: headerBackground,
										}}
									>
										{column.getCanFilter() ? (
											<TextInput
												size="xs"
												value={currentValue}
												onChange={(event) => column.setFilterValue(event.currentTarget.value)}
												placeholder={t("common.search")}
											/>
										) : null}
									</Table.Th>
								);
							})}
						</Table.Tr>
					)}
				</Table.Thead>
				<Table.Tbody>
					{isLoading ? (
						<Table.Tr>
							<Table.Td colSpan={resolvedColumns.length}>
								<Group justify="center" p="md">
									<Loader size="sm" />
								</Group>
							</Table.Td>
						</Table.Tr>
					) : tableInstance.getRowModel().rows.length === 0 ? (
						<Table.Tr>
							<Table.Td colSpan={resolvedColumns.length}>
								<Text c="dimmed" ta="center" py="md">
									{t("common.noData")}
								</Text>
							</Table.Td>
						</Table.Tr>
					) : (
						tableInstance.getRowModel().rows.map((row) => (
							<Table.Tr key={row.id}>
								{row.getVisibleCells().map((cell) => (
									<Table.Td key={cell.id} style={{ paddingBlock: densityPadding }}>
										{flexRender(cell.column.columnDef.cell, cell.getContext())}
									</Table.Td>
								))}
							</Table.Tr>
						))
					)}
				</Table.Tbody>
			</Table>
		</ScrollArea>
	);
}

