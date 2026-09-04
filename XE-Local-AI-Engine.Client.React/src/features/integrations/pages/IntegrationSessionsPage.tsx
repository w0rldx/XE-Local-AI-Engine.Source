import { Alert, Group, Loader, Select, Text } from "@mantine/core";
import { IconAlertTriangle, IconPlug } from "@tabler/icons-react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { TablePaginationFooter } from "@/core/ui/components/TablePagination/TablePaginationFooter";
import { useServerTablePagination } from "@/core/ui/components/TablePagination/useTablePagination";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import { IntegrationSessionDetailDialog } from "@/features/integrations/components/IntegrationSessionDetailDialog";
import { IntegrationSessionList } from "@/features/integrations/components/IntegrationSessionList";
import {
	type IntegrationSession,
	type IntegrationSessionFilters,
	type IntegrationSessionStatus,
	integrationPageSize,
	integrationPageSizeOptions,
	integrationSessionStatuses,
} from "@/features/integrations/models/IntegrationModels";
import {
	useDeleteIntegrationSession,
	useIntegrationSessions,
} from "@/features/integrations/queries/useIntegrationSessions";
import { useIntegrationTriggers } from "@/features/integrations/queries/useIntegrationTriggers";
import { useIntegrationsUiStore } from "@/features/integrations/stores/IntegrationsUiStore";

const ALL_VALUE = "__all__";

/**
 * Caller-managed conversations an integrator holds across invocations. BOTH filters and the paging are server-side:
 * the response is one page of the server's ordering, so narrowing or slicing it in the browser would hide sessions
 * that match the filter but fall on another page. The response's `totalCount` is what the pager numbers.
 */
export function IntegrationSessionsPage() {
	const { t } = useTranslation();
	const { confirm } = useConfirm();

	const [filters, setFilters] = useState<IntegrationSessionFilters>({});
	const [page, setPage] = useState(1);
	const [pageSize, setPageSize] = useState(integrationPageSize);

	// Both filters are server parameters, so a narrowed list is a different list and page 4 of the old one is
	// meaningless against it. A new page size renumbers the pages for the same reason.
	const applyFilters = useCallback((next: (current: IntegrationSessionFilters) => IntegrationSessionFilters) => {
		setFilters(next);
		setPage(1);
	}, []);

	const handlePageSizeChange = useCallback((next: number) => {
		setPageSize(next);
		setPage(1);
	}, []);

	const selectedSessionId = useIntegrationsUiStore((state) => state.selectedSessionId);
	const selectSession = useIntegrationsUiStore((state) => state.actions.selectSession);

	useEffect(() => {
		return () => {
			selectSession(null);
		};
	}, [selectSession]);

	const sessionsQuery = useIntegrationSessions(filters, { limit: pageSize, offset: (page - 1) * pageSize });
	const triggersQuery = useIntegrationTriggers();
	const deleteMutation = useDeleteIntegrationSession();

	const sessions = useMemo(() => sessionsQuery.data?.items ?? [], [sessionsQuery.data]);
	const triggers = useMemo(() => triggersQuery.data ?? [], [triggersQuery.data]);

	// Deleting the last session on a page is the one case the pager has to recover from on its own.
	const pagination = useServerTablePagination({
		page,
		pageSize,
		totalItems: sessionsQuery.data?.totalCount ?? 0,
		pageSizeOptions: integrationPageSizeOptions,
		onPageChange: setPage,
		onPageSizeChange: handlePageSizeChange,
	});

	const selectedSession = sessions.find((session) => session.id === selectedSessionId) ?? null;

	const triggerData = useMemo(
		() => [
			{ value: ALL_VALUE, label: t("pages.integrations.sessions.filters.allTriggers", "All triggers") },
			...triggers.map((trigger) => ({ value: trigger.id, label: trigger.displayName })),
		],
		[t, triggers],
	);

	const statusData = useMemo(
		() => [
			{ value: ALL_VALUE, label: t("pages.integrations.sessions.filters.allStatuses", "All statuses") },
			...integrationSessionStatuses.map((status) => ({
				value: status,
				label: t(`pages.integrations.sessions.status.${status}`, status),
			})),
		],
		[t],
	);

	const handleTriggerChange = useCallback(
		(value: string | null): void => {
			applyFilters((current) => ({ ...current, triggerId: value === null || value === ALL_VALUE ? undefined : value }));
		},
		[applyFilters],
	);

	const handleStatusChange = useCallback(
		(value: string | null): void => {
			applyFilters((current) => ({
				...current,
				status: value === null || value === ALL_VALUE ? undefined : (value as IntegrationSessionStatus),
			}));
		},
		[applyFilters],
	);

	const handleDelete = useCallback(
		async (session: IntegrationSession) => {
			const confirmed = await confirm({
				title: t("pages.integrations.sessions.delete.title", "Delete session"),
				description: t(
					"pages.integrations.sessions.delete.description",
					"Delete this session? Its conversation, its executions and their recorded events are removed. This cannot be undone.",
				),
				confirmationText: t("pages.integrations.sessions.delete.action", "Delete"),
				cancellationText: t("common.cancel", "Cancel"),
			});

			if (confirmed) {
				deleteMutation.mutate(
					{ path: { sessionId: session.id } },
					{
						onSuccess: () => {
							// The dialog would otherwise stay open over a row that no longer exists.
							if (selectedSessionId === session.id) {
								selectSession(null);
							}
						},
						// A 409 says an execution on the session is still Accepted/Queued/Running; the backend's own
						// message names the fix (cancel it first), so it is what the toast shows.
						onError: (error) =>
							toast.error(
								apiErrorMessage(
									error,
									t(
										"pages.integrations.sessions.errors.delete",
										"Could not delete the session. Cancel its active executions first.",
									),
								),
							),
					},
				);
			}
		},
		[confirm, deleteMutation, selectSession, selectedSessionId, t],
	);

	const loadError = sessionsQuery.error
		? apiErrorMessage(sessionsQuery.error, t("pages.integrations.sessions.errors.load", "Could not load integration sessions."))
		: undefined;

	return (
		<PageShell>
			<PageHeader
				title={t("pages.integrations.sessions.title", "Integration sessions")}
				icon={<IconPlug size={24} />}
				subtitle={t(
					"pages.integrations.sessions.subtitle",
					"Conversations a caller-managed trigger keeps across invocations. Deleting one removes its conversation and every execution that ran on it.",
				)}
			/>

			<SectionCard data-testid="integration-sessions-card">
				<Group gap="sm" align="flex-end">
					<Select
						label={t("pages.integrations.sessions.filters.trigger", "Trigger")}
						data={triggerData}
						value={filters.triggerId ?? ALL_VALUE}
						onChange={handleTriggerChange}
						data-testid="integration-sessions-filter-trigger"
					/>
					<Select
						label={t("pages.integrations.sessions.filters.status", "Status")}
						data={statusData}
						value={filters.status ?? ALL_VALUE}
						onChange={handleStatusChange}
						data-testid="integration-sessions-filter-status"
					/>
				</Group>

				{sessionsQuery.isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">{t("pages.integrations.sessions.list.loading", "Loading sessions…")}</Text>
					</Group>
				) : null}
				{loadError ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="integration-sessions-error">
						{loadError}
					</Alert>
				) : null}
				{!(sessionsQuery.isLoading || loadError) ? (
					<>
						<IntegrationSessionList
							sessions={sessions}
							isMutating={deleteMutation.isPending}
							onView={(session) => selectSession(session.id)}
							onDelete={handleDelete}
						/>
						<TablePaginationFooter {...pagination} data-testid="integration-sessions-pagination" />
					</>
				) : null}
			</SectionCard>

			{selectedSession === null ? null : (
				<IntegrationSessionDetailDialog session={selectedSession} onClose={() => selectSession(null)} />
			)}
		</PageShell>
	);
}
