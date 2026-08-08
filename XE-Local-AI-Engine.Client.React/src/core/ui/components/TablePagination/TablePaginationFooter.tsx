import { Group, Pagination, Select, Text } from "@mantine/core";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

export interface TablePaginationFooterProps {
	readonly page: number;
	readonly pageCount: number;
	readonly pageSize: number;
	readonly totalItems: number;
	readonly firstItemIndex: number;
	readonly lastItemIndex: number;
	readonly pageSizeOptions: readonly number[];
	readonly onPageChange: (page: number) => void;
	readonly onPageSizeChange: (pageSize: number) => void;
	readonly "data-testid"?: string;
}

// Presentational footer for any paginated table: a "showing X–Y of Z" range label, a rows-per-page selector, and
// the Mantine page navigator. Holds no state — pair it with useTablePagination, which owns the page/size math. The
// page navigator is hidden when there is a single page so a small table shows only the range + size selector.
export function TablePaginationFooter({
	page,
	pageCount,
	pageSize,
	totalItems,
	firstItemIndex,
	lastItemIndex,
	pageSizeOptions,
	onPageChange,
	onPageSizeChange,
	"data-testid": dataTestId = "table-pagination",
}: TablePaginationFooterProps) {
	const { t } = useTranslation();

	const sizeData = useMemo(
		() => pageSizeOptions.map((size) => ({ value: String(size), label: String(size) })),
		[pageSizeOptions],
	);

	const handleSizeChange = (value: string | null): void => {
		if (value !== null) {
			onPageSizeChange(Number(value));
		}
	};

	return (
		<Group justify="space-between" gap="md" wrap="wrap" data-testid={dataTestId}>
			<Text size="sm" c="dimmed" data-testid={`${dataTestId}-range`}>
				{t("common.pagination.showingRange", "Showing {{from}}–{{to}} of {{total}}", {
					from: firstItemIndex,
					to: lastItemIndex,
					total: totalItems,
				})}
			</Text>
			<Group gap="sm" wrap="nowrap">
				<Select
					aria-label={t("common.pagination.rowsPerPage", "Rows per page")}
					data={sizeData}
					value={String(pageSize)}
					onChange={handleSizeChange}
					size="xs"
					w={80}
					allowDeselect={false}
					comboboxProps={{ withinPortal: true }}
					data-testid={`${dataTestId}-size`}
				/>
				{pageCount > 1 ? (
					<Pagination
						total={pageCount}
						value={page}
						onChange={onPageChange}
						size="sm"
						withEdges={true}
						data-testid={`${dataTestId}-controls`}
					/>
				) : null}
			</Group>
		</Group>
	);
}
