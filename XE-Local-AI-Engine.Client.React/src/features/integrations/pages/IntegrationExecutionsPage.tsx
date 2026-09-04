import { Alert, Chip, Group, Loader, Select, Text } from "@mantine/core";
import { IconAlertTriangle, IconPlug } from "@tabler/icons-react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import { IntegrationExecutionDetailDialog } from "@/features/integrations/components/IntegrationExecutionDetailDialog";
import { IntegrationExecutionTable } from "@/features/integrations/components/IntegrationExecutionTable";
import {
	type IntegrationExecution,
	type IntegrationExecutionFilters,
	type IntegrationExecutionStatus,
	integrationExecutionStatuses,
	integrationListLimit,
} from "@/features/integrations/models/IntegrationModels";
import {
	useCancelIntegrationExecution,
	useIntegrationExecutions,
} from "@/features/integrations/queries/useIntegrationExecutions";
import { useIntegrationSessions } from "@/features/integrations/queries/useIntegrationSessions";
import { useIntegrationTriggers } from "@/features/integrations/queries/useIntegrationTriggers";
import { useIntegrationsUiStore } from "@/features/integrations/stores/IntegrationsUiStore";

const ALL_VALUE = "__all__";

/**
 * Active and historical integration runs on ONE page. There is no Active/History split, because the backend accepts a
 * single `status` and each of those groups covers three: the difference could only have been made up in the browser,
 * over a server-bounded window, hiding rows that match the filter but fall outside it. One chip per state maps 1:1
 * onto the parameter the endpoint actually takes.
 */
export function IntegrationExecutionsPage() {
	const { t } = useTranslation();
	const { confirm } = useConfirm();

	const [filters, setFilters] = useState<IntegrationExecutionFilters>({});

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
	const executionsQuery = useIntegrationExecutions(filters, { refetchInterval: 5000 });
	const triggersQuery = useIntegrationTriggers();
	const sessionsQuery = useIntegrationSessions();
	const cancelMutation = useCancelIntegrationExecution();

	const executions = useMemo(() => executionsQuery.data ?? [], [executionsQuery.data]);
	const triggers = useMemo(() => triggersQuery.data ?? [], [triggersQuery.data]);
	const sessions = useMemo(() => sessionsQuery.data ?? [], [sessionsQuery.data]);

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

	const handleStatusChange = useCallback((value: string): void => {
		setFilters((current) => ({
			...current,
			status: value === ALL_VALUE ? undefined : (value as IntegrationExecutionStatus),
		}));
	}, []);

	const handleTriggerChange = useCallback((value: string | null): void => {
		setFilters((current) => ({ ...current, triggerId: value === null || value === ALL_VALUE ? undefined : value }));
	}, []);

	const handleSessionChange = useCallback((value: string | null): void => {
		setFilters((current) => ({ ...current, sessionId: value === null || value === ALL_VALUE ? undefined : value }));
	}, []);

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
						// A 409 means the run reached a terminal state first — a race, not an operator error. The message
						// the backend sends says so, and the next poll shows what the run actually became.
						onError: (error) =>
							toast.error(
								apiErrorMessage(
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
					<Chip.Group
						multiple={false}
						value={filters.status ?? ALL_VALUE}
						onChange={(value) => handleStatusChange(value as string)}
					>
						<Group
							gap={4}
							role="group"
							aria-label={t("pages.integrations.executions.filters.statusGroup", "Filter executions by status")}
							data-testid="integration-executions-status-chips"
						>
							<Chip value={ALL_VALUE} data-testid="integration-executions-status-all">
								{t("pages.integrations.executions.filters.allStatuses", "All")}
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

				{/* States what the window IS, and never calls it "the latest N": the response carries no total count, so
				    only the server's ordering can be described, not the table's contents. */}
				<Text size="sm" c="dimmed" data-testid="integration-executions-window-note">
					{t("pages.integrations.executions.list.windowNote", {
						defaultValue:
							"Showing up to {{limit}} most recently received executions. Narrow by trigger, session or status to reach older records.",
						limit: integrationListLimit,
					})}
				</Text>

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
					<IntegrationExecutionTable
						executions={executions}
						triggers={triggers}
						isCancelling={cancelMutation.isPending}
						onView={(execution) => selectExecution(execution.id)}
						onCancel={handleCancel}
					/>
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
