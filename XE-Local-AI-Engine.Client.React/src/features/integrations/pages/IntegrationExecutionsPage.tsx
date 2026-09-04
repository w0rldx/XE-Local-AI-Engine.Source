import { Alert, Chip, Group, Loader, Select, Text } from "@mantine/core";
import { IconAlertTriangle, IconPlug } from "@tabler/icons-react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { getErrorStatus } from "@/core/api/errors/RetryClassification";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { TablePaginationFooter } from "@/core/ui/components/TablePagination/TablePaginationFooter";
import { useServerTablePagination } from "@/core/ui/components/TablePagination/useTablePagination";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import { IntegrationExecutionDetailDialog } from "@/features/integrations/components/IntegrationExecutionDetailDialog";
import { IntegrationExecutionTable } from "@/features/integrations/components/IntegrationExecutionTable";
import {
	activeIntegrationExecutionStatuses,
	type IntegrationExecution,
	type IntegrationExecutionFilters,
	type IntegrationExecutionStatus,
	integrationExecutionStatuses,
	integrationPageSize,
	integrationPageSizeOptions,
} from "@/features/integrations/models/IntegrationModels";
import {
	useCancelIntegrationExecution,
	useIntegrationExecutions,
} from "@/features/integrations/queries/useIntegrationExecutions";
import { useIntegrationSessions } from "@/features/integrations/queries/useIntegrationSessions";
import { useIntegrationTriggers } from "@/features/integrations/queries/useIntegrationTriggers";
import { useIntegrationsUiStore } from "@/features/integrations/stores/IntegrationsUiStore";

const ALL_VALUE = "__all__";
const ACTIVE_VALUE = "__active__";

/** The status set one chip sends. All sends none; Active sends the three in-flight states; every other chip sends its own. */
function statusChipFilter(value: string): readonly IntegrationExecutionStatus[] | undefined {
	if (value === ALL_VALUE) {
		return undefined;
	}
	return value === ACTIVE_VALUE ? activeIntegrationExecutionStatuses : [value as IntegrationExecutionStatus];
}

/** Which chip is lit, read back off the filter. Active is the only chip that sends more than one status. */
function statusChipValue(status: readonly IntegrationExecutionStatus[] | undefined): string {
	if (status === undefined) {
		return ALL_VALUE;
	}
	return status.length === 1 ? (status.at(0) ?? ALL_VALUE) : ACTIVE_VALUE;
}

/**
 * Active and historical integration runs on ONE page. Every chip maps onto the `status` parameter the endpoint takes,
 * which is now a SET: one chip per state, plus an Active chip that sends the three in-flight states together. The
 * difference is still never made up in the browser — that would hide rows which match the filter but fall on another
 * page — it is asked of the server, which is also what counts the rows the pager numbers.
 */
export function IntegrationExecutionsPage() {
	const { t } = useTranslation();
	const { confirm } = useConfirm();

	const [filters, setFilters] = useState<IntegrationExecutionFilters>({});
	const [page, setPage] = useState(1);
	const [pageSize, setPageSize] = useState(integrationPageSize);

	// Every filter is a server parameter, so a narrowed list is a DIFFERENT list: staying on page 4 of it would show
	// the operator rows they did not ask to jump to, or nothing at all.
	const applyFilters = useCallback((next: (current: IntegrationExecutionFilters) => IntegrationExecutionFilters) => {
		setFilters(next);
		setPage(1);
	}, []);

	// A new page size renumbers the pages, so the old page number means nothing against it.
	const handlePageSizeChange = useCallback((next: number) => {
		setPageSize(next);
		setPage(1);
	}, []);

	const selectedExecutionId = useIntegrationsUiStore((state) => state.selectedExecutionId);
	const selectExecution = useIntegrationsUiStore((state) => state.actions.selectExecution);

	// Reset on unmount so navigating away and back does not reopen a dialog from stale Zustand state.
	useEffect(() => {
		return () => {
			selectExecution(null);
		};
	}, [selectExecution]);

	// Polling is UNCONDITIONAL. Gating it on "any row is active" would read the very list a poll has to fetch, so an
	// empty or all-terminal window — the first load of a fresh node included — would switch the refresh off and a run
	// started by an integrator elsewhere would never appear.
	const executionsQuery = useIntegrationExecutions(filters, {
		refetchInterval: 5000,
		limit: pageSize,
		offset: (page - 1) * pageSize,
	});
	const triggersQuery = useIntegrationTriggers();
	const sessionsQuery = useIntegrationSessions();
	const cancelMutation = useCancelIntegrationExecution();

	const executions = useMemo(() => executionsQuery.data?.items ?? [], [executionsQuery.data]);
	const triggers = useMemo(() => triggersQuery.data ?? [], [triggersQuery.data]);
	const sessions = useMemo(() => sessionsQuery.data?.items ?? [], [sessionsQuery.data]);

	const pagination = useServerTablePagination({
		page,
		pageSize,
		totalItems: executionsQuery.data?.totalCount ?? 0,
		pageSizeOptions: integrationPageSizeOptions,
		onPageChange: setPage,
		onPageSizeChange: handlePageSizeChange,
	});

	const selectedExecution = executions.find((execution) => execution.id === selectedExecutionId) ?? null;

	const triggerData = useMemo(
		() => [
			{ value: ALL_VALUE, label: t("pages.integrations.executions.filters.allTriggers", "All triggers") },
			...triggers.map((trigger) => ({ value: trigger.id, label: trigger.displayName })),
		],
		[t, triggers],
	);

	const sessionData = useMemo(
		() => [
			{ value: ALL_VALUE, label: t("pages.integrations.executions.filters.allSessions", "All sessions") },
			...sessions.map((session) => ({ value: session.id, label: session.id })),
		],
		[sessions, t],
	);

	const handleStatusChange = useCallback(
		(value: string): void => {
			applyFilters((current) => ({ ...current, status: statusChipFilter(value) }));
		},
		[applyFilters],
	);

	const handleTriggerChange = useCallback(
		(value: string | null): void => {
			applyFilters((current) => ({ ...current, triggerId: value === null || value === ALL_VALUE ? undefined : value }));
		},
		[applyFilters],
	);

	const handleSessionChange = useCallback(
		(value: string | null): void => {
			applyFilters((current) => ({ ...current, sessionId: value === null || value === ALL_VALUE ? undefined : value }));
		},
		[applyFilters],
	);

	const handleCancel = useCallback(
		async (execution: IntegrationExecution) => {
			const confirmed = await confirm({
				title: t("pages.integrations.executions.cancel.title", "Cancel execution"),
				description: t(
					"pages.integrations.executions.cancel.description",
					"Request cancellation of this execution? A running turn stops once it observes the request, so the row may stay active for a moment.",
				),
				confirmationText: t("pages.integrations.executions.cancel.action", "Cancel execution"),
				cancellationText: t("common.cancel", "Cancel"),
			});

			if (confirmed) {
				cancelMutation.mutate(
					{ path: { executionId: execution.id } },
					{
						// A 409 means the run reached a terminal state first — a race, not an operator error. The
						// backend's own text for it is a FastEndpoints validation problem whose only readable field is a
						// generic English title, so this case gets its own localized sentence instead. The refetch that
						// replaces the stale row rides on the mutation's onSettled.
						onError: (error) =>
							toast.error(
								getErrorStatus(error) === 409
									? t(
											"pages.integrations.executions.errors.cancelConflict",
											"This execution had already finished.",
										)
									: apiErrorMessage(
											error,
											t("pages.integrations.executions.errors.cancel", "Could not cancel the execution."),
										),
							),
					},
				);
			}
		},
		[cancelMutation, confirm, t],
	);

	const loadError = executionsQuery.error
		? apiErrorMessage(executionsQuery.error, t("pages.integrations.executions.errors.load", "Could not load integration executions."))
		: undefined;

	return (
		<PageShell>
			<PageHeader
				title={t("pages.integrations.executions.title", "Integration executions")}
				icon={<IconPlug size={24} />}
				subtitle={t(
					"pages.integrations.executions.subtitle",
					"Unattended runs an integrator started through a trigger. Cancel an active run, or open one to read its recorded timeline.",
				)}
			/>

			<SectionCard data-testid="integration-executions-card">
				<Group gap="sm" align="flex-end">
					<Chip.Group multiple={false} value={statusChipValue(filters.status)} onChange={(value) => handleStatusChange(value as string)}>
						<Group
							gap={4}
							role="group"
							aria-label={t("pages.integrations.executions.filters.statusGroup", "Filter executions by status")}
							data-testid="integration-executions-status-chips"
						>
							<Chip value={ALL_VALUE} data-testid="integration-executions-status-all">
								{t("pages.integrations.executions.filters.allStatuses", "All")}
							</Chip>
							{/* One click for everything in flight. It is a real query, not a browser-side union: the endpoint
							    takes a repeated `status`, so the count behind the pager stays the server's. */}
							<Chip value={ACTIVE_VALUE} data-testid="integration-executions-status-active">
								{t("pages.integrations.executions.filters.activeStatuses", "Active")}
							</Chip>
							{integrationExecutionStatuses.map((status) => (
								<Chip key={status} value={status} data-testid={`integration-executions-status-${status}`}>
									{t(`pages.integrations.executions.status.${status}`, status)}
								</Chip>
							))}
						</Group>
					</Chip.Group>
					<Select
						label={t("pages.integrations.executions.filters.trigger", "Trigger")}
						data={triggerData}
						value={filters.triggerId ?? ALL_VALUE}
						onChange={handleTriggerChange}
						data-testid="integration-executions-filter-trigger"
					/>
					<Select
						label={t("pages.integrations.executions.filters.session", "Session")}
						data={sessionData}
						value={filters.sessionId ?? ALL_VALUE}
						onChange={handleSessionChange}
						data-testid="integration-executions-filter-session"
					/>
				</Group>

				{executionsQuery.isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">{t("pages.integrations.executions.list.loading", "Loading executions…")}</Text>
					</Group>
				) : null}
				{loadError ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="integration-executions-error">
						{loadError}
					</Alert>
				) : null}
				{!(executionsQuery.isLoading || loadError) ? (
					<>
						<IntegrationExecutionTable
							executions={executions}
							triggers={triggers}
							isCancelling={cancelMutation.isPending}
							onView={(execution) => selectExecution(execution.id)}
							onCancel={handleCancel}
						/>
						{/* Server-side: `totalCount` counts every row THESE filters match, so the range and the page count
						    describe the whole table rather than the window that happened to load. */}
						<TablePaginationFooter {...pagination} data-testid="integration-executions-pagination" />
					</>
				) : null}
			</SectionCard>

			{/* Rendered only while a row is selected, so closing the dialog unmounts its two queries and stops their
			    poll rather than leaving them refetching behind a hidden modal. It is mounted on the SELECTED ID, not on
			    the row found in the current window: a poll returning a window without that row must not close it. */}
			{selectedExecutionId === null ? null : (
				<IntegrationExecutionDetailDialog
					key={selectedExecutionId}
					executionId={selectedExecutionId}
					listExecution={selectedExecution}
					onClose={() => selectExecution(null)}
				/>
			)}
		</PageShell>
	);
}
