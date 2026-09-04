import { Alert, Code, Divider, Group, Loader, Stack, Text, Tooltip } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useRef } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { formatIntegrationDuration, shortPrincipalId } from "@/features/integrations/components/IntegrationFormatters";
import { IntegrationExecutionStatusBadge } from "@/features/integrations/components/IntegrationStatusBadge";
import { IntegrationExecutionTimeline } from "@/features/integrations/components/IntegrationExecutionTimeline";
import {
	type IntegrationExecution,
	type IntegrationExecutionStatus,
	isActiveExecutionStatus,
} from "@/features/integrations/models/IntegrationModels";
import {
	useIntegrationExecution,
	useIntegrationExecutionEvents,
} from "@/features/integrations/queries/useIntegrationExecutions";

interface IntegrationExecutionDetailDialogProps {
	/** The selected id — what the dialog is keyed and mounted on, never a row object. */
	executionId: string;
	/** The row from the current list window, while it still holds one. A seed only: the detail read outranks it. */
	listExecution: IntegrationExecution | null;
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
 *
 * What it renders comes from the PER-EXECUTION read wherever that read has answered, so a list poll returning a window
 * that no longer contains the row leaves the open dialog alone instead of closing it mid-read.
 */
export function IntegrationExecutionDetailDialog({
	executionId,
	listExecution,
	onClose,
}: IntegrationExecutionDetailDialogProps) {
	const { t } = useTranslation();

	// The detail poll's interval has to be decided BEFORE the read it governs, so the status behind it comes from the
	// list row while there is one and otherwise from whatever the previous render resolved.
	const lastKnownStatus = useRef<IntegrationExecutionStatus | null>(listExecution?.status ?? null);
	const pollStatus = listExecution?.status ?? lastKnownStatus.current;

	// `outputBytes` keeps growing while the run writes outputs, so an active run's audit block polls beside the
	// timeline rather than staying frozen at the value it had when the dialog opened.
	const detailQuery = useIntegrationExecution(executionId, {
		refetchInterval: pollStatus !== null && isActiveExecutionStatus(pollStatus) ? 5000 : undefined,
	});
	const eventsQuery = useIntegrationExecutionEvents(executionId, { refetchInterval: 5000 });

	const detail = detailQuery.data;
	const execution = detail?.execution ?? listExecution;
	lastKnownStatus.current = execution?.status ?? lastKnownStatus.current;
	const events = eventsQuery.data ?? [];

	const detailError = detailQuery.error
		? apiErrorMessage(
				detailQuery.error,
				t("pages.integrations.executions.errors.detail", "Could not load the execution audit detail."),
			)
		: undefined;
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
				{execution === null ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">{t("pages.integrations.executions.list.loading", "Loading executions…")}</Text>
					</Group>
				) : (
					<>
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
							<Text size="sm">{formatTimestamp(execution.receivedAtUtc)}</Text>
						</DetailRow>
						<DetailRow label={t("pages.integrations.executions.list.columns.started", "Started")}>
							<Text size="sm">{formatTimestamp(execution.startedAtUtc)}</Text>
						</DetailRow>
						<DetailRow label={t("pages.integrations.executions.list.columns.ended", "Ended")}>
							<Text size="sm">{formatTimestamp(execution.endedAtUtc)}</Text>
						</DetailRow>
						<DetailRow label={t("pages.integrations.executions.list.columns.duration", "Duration")}>
							<Text size="sm">{formatIntegrationDuration(execution.startedAtUtc, execution.endedAtUtc)}</Text>
						</DetailRow>
						<DetailRow label={t("pages.integrations.executions.list.columns.outputs", "Outputs")}>
							<Text size="sm">{execution.outputCount}</Text>
						</DetailRow>
					</>
				)}

				{/* The audit half rides only on the per-execution read: `principalId` says WHICH integrator invoked the
				    run, and the keys page is where that identity maps back to a credential. A failed read has to say
				    so — this dialog is the operator's only path to that identity, so silence would look like slowness. */}
				{detailError === undefined ? null : (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="integration-execution-detail-error">
						{detailError}
					</Alert>
				)}

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

				{execution === null || (execution.failureCategory === null && execution.failureSummary === null) ? null : (
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
