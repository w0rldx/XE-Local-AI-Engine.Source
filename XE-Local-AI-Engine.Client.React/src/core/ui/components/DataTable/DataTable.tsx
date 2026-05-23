import {
	ActionIcon,
	Alert,
	Button,
	Divider,
	Group,
	Modal,
	Paper,
	Text,
	Title,
	Tooltip,
} from "@mantine/core";
import { IconEdit, IconEye, IconInfoCircle, IconTrash } from "@tabler/icons-react";
import { useQuery } from "@tanstack/react-query";
import { functionalUpdate } from "@tanstack/react-router";
import { type ColumnDef, getCoreRowModel, type OnChangeFn, useReactTable } from "@tanstack/react-table";
import type { ReactNode } from "react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { useAppTheme as useTheme } from "@/core/theme/hooks/useAppTheme";
import type {
	DataTableColumnFiltersState,
	DataTableEntityRow,
	DataTablePaginationState,
	DataTableRow,
	DataTableSortingState,
	DataTableTableInstance,
	DataTableVisibilityState,
	IDataTableProperties,
	PaginatedQueryFunction,
	SimpleQueryFunction,
} from "@/core/ui/components/DataTable/Types";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import useTableState from "@/core/ui/hooks/useTableState";
import type { TableFilterPagedResult } from "@/core/ui/models/TableFilterResult";
import { toast } from "@/core/ui/notifications/Toast";
import { DialogPaperPropertiesStyle } from "@/core/ui/styles/DialogPaperPropertiesStyle";
import {
	getDensityIcon,
	getDensityPadding,
	getDensityTableSpacing,
	getNextDensity,
} from "@/core/ui/components/DataTable/DataTable.helpers";
import { DataTableToolbar } from "@/core/ui/components/DataTable/DataTable.Toolbar";
import { DataTableBody } from "@/core/ui/components/DataTable/DataTable.Body";
import { DataTablePagination } from "@/core/ui/components/DataTable/DataTable.Pagination";

const EMPTY_ROW_ACTIONS: never[] = [];

export function DataTable<TData extends DataTableEntityRow>({
	tableKey,
	title,
	subtitle,
	columns,
	queryKey,
	queryFn,
	queryMode = "simple",
	actionCapabilities,
	createButtonText,
	onCreateClick,
	editTooltip,
	deleteTooltip,
	viewTooltip,
	getEditActionState,
	getDeleteActionState,
	getViewActionState,
	createModalComponent,
	editModalComponent,
	viewModalComponent,
	deleteMutation,
	deleteDialogTitle,
	deleteDialogText,
	deleteSuccessMessage,
	customRowActions = EMPTY_ROW_ACTIONS,
	displayOptions,
	maxHeight,
	additionalToolbarActions,
	tableRef,
	tableOptions,
}: IDataTableProperties<TData>) {
	const {
		columnFilters,
		globalFilter,
		sorting,
		columnVisibility,
		pagination,
		density,
		setColumnFilters,
		setGlobalFilter,
		setSorting,
		setColumnVisibility,
		setPagination,
		setDensity,
	} = useTableState(tableKey);

	const { t } = useTranslation();
	const { confirm } = useConfirm();
	const theme = useTheme();
	const dialogContentStyle = DialogPaperPropertiesStyle(theme);
	const isPaginatedQuery = queryMode === "paginated";
	const canCreate = actionCapabilities?.create ?? false;
	const canEdit = actionCapabilities?.edit ?? false;
	const canDelete = actionCapabilities?.delete ?? false;
	const canView = actionCapabilities?.view ?? false;
	const enableRowNumbers = displayOptions?.rowNumbers ?? false;
	const showRowActionsInColumnToggle = displayOptions?.showRowActionsInColumnToggle ?? false;
	const [showGlobalFilter, setShowGlobalFilter] = useState(globalFilter !== "");
	const [showColumnFilters, setShowColumnFilters] = useState(columnFilters.length > 0);
	const [editingRow, setEditingRow] = useState<DataTableRow<TData> | null>(null);
	const [creatingRow, setCreatingRow] = useState(false);

	// Keep a stable ref to queryFn so it never bloats the query key or
	// triggers spurious refetches when the parent re-renders.
	const queryFnRef = useRef(queryFn);
	queryFnRef.current = queryFn;

	const {
		data: { items = [], totalCount } = {},
		isError,
		isRefetching,
		isLoading,
	} = useQuery<TableFilterPagedResult<TData[]>>({
		queryKey: [...queryKey, isPaginatedQuery, pagination.pageIndex, pagination.pageSize, globalFilter, sorting, columnFilters],
		queryFn: async () => {
			if (isPaginatedQuery) {
				const page = pagination.pageIndex + 1;
				return (queryFnRef.current as PaginatedQueryFunction<TData>)(
					page,
					pagination.pageSize,
					globalFilter,
					sorting,
					columnFilters,
				);
			}

			const response = await (queryFnRef.current as SimpleQueryFunction<TData>)();
			return {
				items: response,
				totalCount: response.length,
			} as TableFilterPagedResult<TData[]>;
		},
	});

	const hasRowActions = canEdit || canDelete || canView || customRowActions.length > 0;

	const openDeleteConfirmModal = useCallback(
		async (row: DataTableRow<TData>) => {
			if (!deleteMutation || !deleteDialogTitle || !deleteDialogText) {
				return;
			}

			const confirmed = await confirm({
				title: deleteDialogTitle,
				description: deleteDialogText,
				confirmationText: t("common.confirm"),
				cancellationText: t("common.cancel"),
			});

			if (confirmed) {
				await deleteMutation.mutateAsync(row.original.id.toString(), {
					onSuccess: () => {
						if (deleteSuccessMessage) {
							toast.success(deleteSuccessMessage);
						}
					},
				});
			}
		},
		[confirm, deleteDialogText, deleteDialogTitle, deleteMutation, deleteSuccessMessage, t],
	);

	const onResetFilterClick = useCallback(() => {
		setColumnFilters([]);
		setSorting([]);
		setGlobalFilter("");
		setColumnVisibility({});
	}, [setColumnFilters, setColumnVisibility, setGlobalFilter, setSorting]);

	// Stable handler so the create button never gets a new function reference.
	const handleCreateClick = useCallback(() => {
		if (onCreateClick) {
			onCreateClick();
		} else {
			setCreatingRow(true);
		}
	}, [onCreateClick]);

	const toggleGlobalFilter = useCallback(() => setShowGlobalFilter((v) => !v), []);
	const toggleColumnFilters = useCallback(() => setShowColumnFilters((v) => !v), []);

	// Row-number cell reads from a ref so column definitions don't need to be
	// rebuilt every time the page index / size changes.
	const paginationRef = useRef(pagination);
	paginationRef.current = pagination;

	const resolvedColumns = useMemo<ColumnDef<TData>[]>(() => {
		const mappedColumns = [...columns] as ColumnDef<TData>[];

		if (enableRowNumbers) {
			mappedColumns.unshift({
				id: "row-number",
				header: "#",
				enableSorting: false,
				enableColumnFilter: false,
				size: 50,
				cell: ({ row }) => row.index + 1 + paginationRef.current.pageIndex * paginationRef.current.pageSize,
			});
		}

		if (hasRowActions) {
			mappedColumns.push({
				id: "row-actions",
				header: "",
				enableSorting: false,
				enableColumnFilter: false,
				size: 140,
				cell: ({ row }) => (
					<Group gap="xs" wrap="nowrap" justify="flex-end" w="100%" style={{ marginRight: "-20px" }}>
						{canEdit &&
							(() => {
								const actionState = getEditActionState?.(row);
								const isHidden = actionState?.condition ? !actionState.condition(row) : false;

								if (isHidden) {
									return null;
								}

								const isDisabled = actionState?.disabled ?? false;
								const tooltip = actionState?.tooltip || editTooltip || t("common.edit");

								return (
									<Tooltip label={tooltip}>
										<span>
											<ActionIcon disabled={isDisabled} onClick={() => setEditingRow(row)} variant="subtle">
												<IconEdit size={16} />
											</ActionIcon>
										</span>
									</Tooltip>
								);
							})()}
						{canView &&
							!canEdit &&
							(() => {
								const actionState = getViewActionState?.(row);
								const isHidden = actionState?.condition ? !actionState.condition(row) : false;

								if (isHidden) {
									return null;
								}

								const isDisabled = actionState?.disabled ?? false;
								const tooltip = actionState?.tooltip || viewTooltip || t("common.view");

								return (
									<Tooltip label={tooltip}>
										<span>
											<ActionIcon disabled={isDisabled} onClick={() => setEditingRow(row)} variant="subtle">
												<IconEye size={16} />
											</ActionIcon>
										</span>
									</Tooltip>
								);
							})()}
						{canDelete &&
							(() => {
								const actionState = getDeleteActionState?.(row);
								const isHidden = actionState?.condition ? !actionState.condition(row) : false;

								if (isHidden) {
									return null;
								}

								const isDisabled = actionState?.disabled ?? false;
								const tooltip = actionState?.tooltip || deleteTooltip || t("common.delete");

								return (
									<Tooltip label={tooltip}>
										<span>
											<ActionIcon disabled={isDisabled} onClick={() => openDeleteConfirmModal(row)} variant="subtle" color="red">
												<IconTrash size={16} />
											</ActionIcon>
										</span>
									</Tooltip>
								);
							})()}
						{customRowActions.map((action) => {
							if (action.condition && !action.condition(row)) {
								return null;
							}

							return (
								<Tooltip key={action.tooltip} label={action.tooltip}>
									<ActionIcon onClick={() => action.onClick(row)} variant="subtle">
										{action.icon}
									</ActionIcon>
								</Tooltip>
							);
						})}
					</Group>
				),
			});
		}

		return mappedColumns;
	}, [
		canDelete,
		canEdit,
		canView,
		columns,
		customRowActions,
		deleteTooltip,
		editTooltip,
		enableRowNumbers,
		getDeleteActionState,
		getEditActionState,
		getViewActionState,
		hasRowActions,
		openDeleteConfirmModal,
		t,
		viewTooltip,
	]);

	const pageCount = Math.max(1, Math.ceil((totalCount ?? 0) / Math.max(1, pagination.pageSize)));

	const onPaginationChange: OnChangeFn<DataTablePaginationState> = useCallback(
		(updater) => setPagination(functionalUpdate(updater, pagination)),
		[pagination, setPagination],
	);

	const onSortingChange: OnChangeFn<DataTableSortingState> = useCallback(
		(updater) => setSorting(functionalUpdate(updater, sorting)),
		[sorting, setSorting],
	);

	const onColumnFiltersChange: OnChangeFn<DataTableColumnFiltersState> = useCallback(
		(updater) => setColumnFilters(functionalUpdate(updater, columnFilters)),
		[columnFilters, setColumnFilters],
	);

	const onColumnVisibilityChange: OnChangeFn<DataTableVisibilityState> = useCallback(
		(updater) => setColumnVisibility(functionalUpdate(updater, columnVisibility)),
		[columnVisibility, setColumnVisibility],
	);

	const onGlobalFilterChange = useCallback((nextValue: string) => setGlobalFilter(nextValue ?? ""), [setGlobalFilter]);

	const table = useReactTable<TData>({
		columns: resolvedColumns,
		data: items,
		state: {
			sorting,
			columnFilters,
			globalFilter,
			columnVisibility,
			pagination,
		},
		onPaginationChange,
		onSortingChange,
		onColumnFiltersChange,
		onColumnVisibilityChange,
		onGlobalFilterChange,
		manualPagination: true,
		manualSorting: true,
		manualFiltering: true,
		getCoreRowModel: getCoreRowModel(),
		pageCount,
		enableSorting: tableOptions?.enableSorting ?? true,
		enableColumnFilters: tableOptions?.enableColumnFilters ?? true,
		enableHiding: tableOptions?.enableHiding ?? true,
	});

	// Extend the table instance with modal controls once, keeping a stable reference.
	const setCreatingRowForTable = useCallback((nextCreatingRow: boolean | null) => {
		setCreatingRow(Boolean(nextCreatingRow));
	}, []);

	const tableInstance = useMemo<DataTableTableInstance<TData>>(
		() => Object.assign(table, { setEditingRow, setCreatingRow: setCreatingRowForTable }),
		// `table` identity is stable within a render cycle; `setEditingRow` and
		// `setCreatingRowForTable` are stable state/callback references.
		// eslint-disable-next-line react-hooks/exhaustive-deps
		[table, setCreatingRowForTable],
	);

	useEffect(() => {
		if (!tableRef) {
			return;
		}

		tableRef.current = tableInstance;

		return () => {
			tableRef.current = null;
		};
	}, [tableInstance, tableRef]);

	// Derived display values — cheap calculations, no memoisation needed.
	const defaultMaxHeight = hasRowActions ? "calc(100dvh - 30dvh)" : "calc(100dvh - 26dvh)";
	const tableBorderColor = "var(--mantine-color-default-border)";
	const tableBackground = "var(--mantine-color-body)";
	const toolbarBackground = "var(--mantine-color-default-hover)";
	const headerBackground = "var(--mantine-color-default-hover)";

	const densityPadding = getDensityPadding(density);
	const tableSpacing = getDensityTableSpacing(density);
	const nextDensity = getNextDensity(density);
	const densityIcon = getDensityIcon(density);

	const densityTooltipLabel =
		density === "xs"
			? `${t("common.density")}: ${t("common.densityCompact")}`
			: density === "lg"
				? `${t("common.density")}: ${t("common.densitySpacious")}`
				: `${t("common.density")}: ${t("common.densityComfortable")}`;

	const onDensityClick = useCallback(() => setDensity(nextDensity), [nextDensity, setDensity]);

	// Modal content — only computed when the relevant modal is open.
	let editingModalContent: ReactNode = null;
	if (editingRow && canEdit && editModalComponent) {
		const editActionState = getEditActionState?.(editingRow);

		if (!editActionState?.disabled) {
			const EditModalComponent = editModalComponent;
			editingModalContent = <EditModalComponent row={editingRow} table={tableInstance} />;
		}
	} else if (editingRow && canView && viewModalComponent) {
		const ViewModalComponent = viewModalComponent;
		editingModalContent = <ViewModalComponent row={editingRow} table={tableInstance} />;
	}

	let creatingModalContent: ReactNode = null;
	if (createModalComponent) {
		const CreateModalComponent = createModalComponent;
		creatingModalContent = <CreateModalComponent table={tableInstance} />;
	}

	const statusText = isRefetching ? t("common.refreshing") : isError ? t("common.error") : "";

	const handleCloseCreatingModal = useCallback(() => setCreatingRow(false), []);
	const handleCloseEditingModal = useCallback(() => setEditingRow(null), []);

	return (
		<div className="flex flex-col gap-2">
			<Paper withBorder={false} radius="md" p={0} style={{ overflow: "hidden", backgroundColor: tableBackground }}>
				<div className="py-3">
					<Group justify="space-between" wrap="wrap" gap="sm">
						<div>
							<Title order={3}>{title}</Title>
							{subtitle && (
								<Text size="sm" c="dimmed">
									{subtitle}
								</Text>
							)}
						</div>
						{canCreate && (
							<Button variant="filled" color="primary" onClick={handleCreateClick}>
								{createButtonText || t("common.create")}
							</Button>
						)}
					</Group>
				</div>
				<Paper withBorder={true} radius="md" p={0} style={{ overflow: "hidden", backgroundColor: tableBackground }}>
					<div className="px-3 py-2" style={{ backgroundColor: toolbarBackground }}>
						<DataTableToolbar
							globalFilter={globalFilter}
							showGlobalFilter={showGlobalFilter}
							showColumnFilters={showColumnFilters}
							showRowActionsInColumnToggle={showRowActionsInColumnToggle}
							density={density}
							densityTooltipLabel={densityTooltipLabel}
							densityIcon={densityIcon}
							tableInstance={tableInstance}
							tableOptions={tableOptions}
							additionalToolbarActions={additionalToolbarActions}
							onGlobalFilterChange={onGlobalFilterChange}
							onToggleGlobalFilter={toggleGlobalFilter}
							onToggleColumnFilters={toggleColumnFilters}
							onDensityClick={onDensityClick}
							onResetFilterClick={onResetFilterClick}
						/>
					</div>
					<Divider />
					<DataTableBody
						tableInstance={tableInstance}
						resolvedColumns={resolvedColumns}
						columnFilters={columnFilters}
						showColumnFilters={showColumnFilters}
						isLoading={isLoading}
						densityPadding={densityPadding}
						tableSpacing={tableSpacing}
						headerBackground={headerBackground}
						tableBorderColor={tableBorderColor}
						maxHeight={maxHeight}
						defaultMaxHeight={defaultMaxHeight}
					/>
					<Divider />
					<div className="px-3 py-2" style={{ backgroundColor: toolbarBackground }}>
						<DataTablePagination
							pagination={pagination}
							pageCount={pageCount}
							totalCount={totalCount}
							statusText={statusText}
							onSetPagination={setPagination}
						/>
					</div>
				</Paper>
			</Paper>

			{isError && (
				<Alert variant="light" color="red" title={t("common.error")} icon={<IconInfoCircle size={16} />}>
					{t("common.fetchError")}
				</Alert>
			)}

			<Modal
				opened={creatingRow && Boolean(createModalComponent)}
				onClose={handleCloseCreatingModal}
				size="lg"
				zIndex={300}
				closeOnClickOutside={false}
				withCloseButton={false}
				styles={{ content: dialogContentStyle }}
			>
				{creatingModalContent}
			</Modal>

			<Modal
				opened={editingRow !== null}
				onClose={handleCloseEditingModal}
				size="lg"
				zIndex={300}
				closeOnClickOutside={false}
				withCloseButton={false}
				styles={{ content: dialogContentStyle }}
			>
				{editingModalContent}
			</Modal>
		</div>
	);
}
