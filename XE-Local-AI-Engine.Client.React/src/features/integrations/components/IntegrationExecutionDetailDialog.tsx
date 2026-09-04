import { Alert, Code, Divider, Group, Stack, Text, Tooltip } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import {
	formatIntegrationDuration,
	formatIntegrationOptionalTimestamp,
	formatIntegrationTimestamp,
	shortPrincipalId,
} from "@/features/integrations/components/IntegrationFormatters";
import { IntegrationExecutionStatusBadge } from "@/features/integrations/components/IntegrationStatusBadge";
import { IntegrationExecutionTimeline } from "@/features/integrations/components/IntegrationExecutionTimeline";
import type { IntegrationExecution } from "@/features/integrations/models/IntegrationModels";
import {
	useIntegrationExecution,
	useIntegrationExecutionEvents,
} from "@/features/integrations/queries/useIntegrationExecutions";

interface IntegrationExecutionDetailDialogProps {
	execution: IntegrationExecution;
	onClose: () => void;
}

/** One labelled fact of the execution record. */
function DetailRow({ label, children }: { label: string; children: React.ReactNode }) {
	return (
		<Group gap="sm" wrap="nowrap">
			<Text size="sm" c="dimmed" w={160}>
				{label}
			</Text>
			{children}
		</Group>
	);
}

/**
 * The execution record plus its persisted timeline. The dialog owns both reads, so mounting starts the 5000 ms poll
 * and closing unmounts the queries and stops it — no effect has to remember to tear anything down.
 */
export function IntegrationExecutionDetailDialog({ execution, onClose }: IntegrationExecutionDetailDialogProps) {
	const { t } = useTranslation();

	const detailQuery = useIntegrationExecution(execution.id);
	const eventsQuery = useIntegrationExecutionEvents(execution.id, { refetchInterval: 5000 });

	const detail = detailQuery.data;
	const events = eventsQuery.data ?? [];

	const eventsError = eventsQuery.error
		? apiErrorMessage(eventsQuery.error, t("pages.integrations.executions.errors.events", "Could not load the execution timeline."))
		: undefined;

	return (
		<DialogShell
			opened={true}
			onClose={onClose}
			title={t("pages.integrations.executions.detail.title", "Execution detail")}
			size="xl"
			data-testid="integration-execution-detail"
		>
			<Stack gap="sm">
				<DetailRow label={t("pages.integrations.executions.list.columns.status", "Status")}>
					<IntegrationExecutionStatusBadge status={execution.status} />
				</DetailRow>
				<DetailRow label={t("pages.integrations.executions.list.columns.execution", "Execution")}>
					<Code>{execution.id}</Code>
				</DetailRow>
				<DetailRow label={t("pages.integrations.executions.list.columns.session", "Session")}>
					<Code>{execution.sessionId}</Code>
				</DetailRow>
				<DetailRow label={t("pages.integrations.executions.list.columns.received", "Received")}>
					<Text size="sm">{formatIntegrationTimestamp(execution.receivedAtUtc)}</Text>
				</DetailRow>
				<DetailRow label={t("pages.integrations.executions.list.columns.started", "Started")}>
					<Text size="sm">{formatIntegrationOptionalTimestamp(execution.startedAtUtc)}</Text>
				</DetailRow>
				<DetailRow label={t("pages.integrations.executions.list.columns.ended", "Ended")}>
					<Text size="sm">{formatIntegrationOptionalTimestamp(execution.endedAtUtc)}</Text>
				</DetailRow>
				<DetailRow label={t("pages.integrations.executions.list.columns.duration", "Duration")}>
					<Text size="sm">{formatIntegrationDuration(execution.startedAtUtc, execution.endedAtUtc)}</Text>
				</DetailRow>
				<DetailRow label={t("pages.integrations.executions.list.columns.outputs", "Outputs")}>
					<Text size="sm">{execution.outputCount}</Text>
				</DetailRow>

				{/* The audit half rides only on the per-execution read: `principalId` says WHICH integrator invoked the
				    run, and the keys page is where that identity maps back to a credential. */}
				{detail === undefined ? null : (
					<>
						<DetailRow label={t("pages.integrations.executions.detail.principal", "Principal")}>
							<Tooltip label={detail.principalId}>
								<Code title={detail.principalId} data-testid="integration-execution-principal">
									{shortPrincipalId(detail.principalId)}
								</Code>
							</Tooltip>
						</DetailRow>
						<DetailRow label={t("pages.integrations.executions.detail.requestId", "Request id")}>
							<Code data-testid="integration-execution-request-id">{detail.requestId}</Code>
						</DetailRow>
						<DetailRow label={t("pages.integrations.executions.detail.keyPrefix", "Key")}>
							<Code>{`${detail.keyPrefix}…`}</Code>
						</DetailRow>
						<DetailRow label={t("pages.integrations.executions.detail.outputBytes", "Output bytes")}>
							<Text size="sm">{detail.outputBytes}</Text>
						</DetailRow>
					</>
				)}

				{execution.failureCategory === null && execution.failureSummary === null ? null : (
					<Alert
						color="red"
						icon={<IconAlertTriangle size={16} />}
						title={t("pages.integrations.executions.detail.failureTitle", "Failure")}
						data-testid="integration-execution-failure"
					>
						<Stack gap={2}>
							{execution.failureCategory === null ? null : (
								<Text size="sm">
									{`${t("pages.integrations.executions.detail.failureCategory", "Failure category")}: ${execution.failureCategory}`}
								</Text>
							)}
							{execution.failureSummary === null ? null : <Text size="sm">{execution.failureSummary}</Text>}
						</Stack>
					</Alert>
				)}

				<Divider label={t("pages.integrations.executions.detail.timelineTitle", "Timeline")} labelPosition="left" />

				{eventsError === undefined ? null : (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="integration-execution-events-error">
						{eventsError}
					</Alert>
				)}

				<IntegrationExecutionTimeline events={events} isLoading={eventsQuery.isLoading} />
			</Stack>
		</DialogShell>
	);
}
