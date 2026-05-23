import { Group, Pagination, Select, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type { DataTablePaginationState } from "@/core/ui/components/DataTable/Types";
import { PAGE_SIZE_OPTIONS } from "@/core/ui/components/DataTable/DataTable.helpers";

interface DataTablePaginationProps {
	pagination: DataTablePaginationState;
	pageCount: number;
	totalCount: number | undefined;
	statusText: string;
	onSetPagination: (next: DataTablePaginationState) => void;
}

export function DataTablePagination({
	pagination,
	pageCount,
	totalCount,
	statusText,
	onSetPagination,
}: DataTablePaginationProps) {
	const { t } = useTranslation();

	return (
		<Group justify="space-between" wrap="wrap">
			<Group gap="sm">
				<Text size="sm" c="dimmed">
					{statusText}
				</Text>
				<Text size="sm" c="dimmed">
					{`${totalCount ?? 0} ${t("common.entries")}`}
				</Text>
			</Group>
			<Group gap="sm">
				<Select
					w={90}
					data={PAGE_SIZE_OPTIONS.map((value) => ({ value, label: value }))}
					value={String(pagination.pageSize)}
					onChange={(value) => {
						const nextPageSize = Number(value);
						if (!Number.isNaN(nextPageSize) && nextPageSize > 0) {
							onSetPagination({ pageIndex: 0, pageSize: nextPageSize });
						}
					}}
				/>
				<Pagination
					total={pageCount}
					value={pagination.pageIndex + 1}
					onChange={(page) => onSetPagination({ pageIndex: page - 1, pageSize: pagination.pageSize })}
					withEdges={true}
					size="sm"
				/>
			</Group>
		</Group>
	);
}
