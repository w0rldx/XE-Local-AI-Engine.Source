import { ActionIcon, Checkbox, Group, Menu, TextInput, Tooltip } from "@mantine/core";
import { IconColumns, IconFilter, IconSearch, IconZoomReset } from "@tabler/icons-react";
import { AnimatePresence, m } from "framer-motion";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";

import type { DataTableDensityState, DataTableEntityRow, DataTableTableInstance } from "@/core/ui/components/DataTable/Types";

interface DataTableToolbarProps<TData extends DataTableEntityRow> {
	globalFilter: string;
	showGlobalFilter: boolean;
	showColumnFilters: boolean;
	showRowActionsInColumnToggle: boolean;
	density: DataTableDensityState;
	densityTooltipLabel: string;
	densityIcon: ReactNode;
	tableInstance: DataTableTableInstance<TData>;
	tableOptions?: { enableHiding?: boolean };
	additionalToolbarActions?: (table: DataTableTableInstance<TData>) => ReactNode;
	onGlobalFilterChange: (value: string) => void;
	onToggleGlobalFilter: () => void;
	onToggleColumnFilters: () => void;
	onDensityClick: () => void;
	onResetFilterClick: () => void;
}

export function DataTableToolbar<TData extends DataTableEntityRow>({
	globalFilter,
	showGlobalFilter,
	showColumnFilters,
	showRowActionsInColumnToggle,
	density: _density,
	densityTooltipLabel,
	densityIcon,
	tableInstance,
	tableOptions,
	additionalToolbarActions,
	onGlobalFilterChange,
	onToggleGlobalFilter,
	onToggleColumnFilters,
	onDensityClick,
	onResetFilterClick,
}: DataTableToolbarProps<TData>) {
	const { t } = useTranslation();

	return (
		<Group justify="flex-end" wrap="wrap" gap="sm" w="100%">
			<Group gap="xs" wrap="wrap">
				<Group gap="xs" wrap="nowrap">
					<AnimatePresence initial={false}>
						{showGlobalFilter && (
							<m.div
								initial={{ opacity: 0, width: 0, x: -8 }}
								animate={{ opacity: 1, width: 260, x: 0 }}
								exit={{ opacity: 0, width: 0, x: -8 }}
								transition={{ duration: 0.18, ease: "easeOut" }}
								style={{ overflow: "hidden" }}
							>
								<TextInput
									w={260}
									size="xs"
									value={globalFilter}
									onChange={(event) => onGlobalFilterChange(event.currentTarget.value)}
									placeholder={t("common.search")}
									leftSection={<IconSearch size={16} />}
								/>
							</m.div>
						)}
					</AnimatePresence>
					<Tooltip label={t("common.search")}>
						<ActionIcon variant={showGlobalFilter ? "filled" : "light"} onClick={onToggleGlobalFilter}>
							<IconSearch size={16} />
						</ActionIcon>
					</Tooltip>
				</Group>
				<Tooltip label={t("common.filters")}>
					<ActionIcon variant={showColumnFilters ? "filled" : "light"} onClick={onToggleColumnFilters}>
						<IconFilter size={16} />
					</ActionIcon>
				</Tooltip>
				<Menu shadow="md" width={220}>
					<Menu.Target>
						<ActionIcon
							variant="light"
							disabled={!tableOptions?.enableHiding && tableOptions?.enableHiding !== undefined}
						>
							<IconColumns size={16} />
						</ActionIcon>
					</Menu.Target>
					<Menu.Dropdown>
						{tableInstance.getAllLeafColumns().map((column) =>
							column.getCanHide() && (showRowActionsInColumnToggle || column.id !== "row-actions") ? (
								<Menu.Item key={column.id} component="div" closeMenuOnClick={false}>
									<Checkbox
										label={column.columnDef.header?.toString() || column.id}
										checked={column.getIsVisible()}
										onChange={(event) => column.toggleVisibility(event.currentTarget.checked)}
									/>
								</Menu.Item>
							) : null,
						)}
					</Menu.Dropdown>
				</Menu>
				<Tooltip label={densityTooltipLabel}>
					<ActionIcon variant="light" onClick={onDensityClick}>
						{densityIcon}
					</ActionIcon>
				</Tooltip>
				<Tooltip label={t("common.filterReset")}>
					<ActionIcon variant="light" onClick={onResetFilterClick}>
						<IconZoomReset size={16} />
					</ActionIcon>
				</Tooltip>
				{additionalToolbarActions?.(tableInstance)}
			</Group>
		</Group>
	);
}
