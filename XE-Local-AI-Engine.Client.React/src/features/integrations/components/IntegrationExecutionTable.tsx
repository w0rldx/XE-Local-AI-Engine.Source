import { ActionIcon, Badge, Code, Group, Text, Tooltip } from "@mantine/core";
import { IconEye, IconPlayerStop } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { ResponsiveTable } from "@/core/ui/components/ResponsiveTable/ResponsiveTable";
import {
	formatFailureCategory,
	formatIntegrationDuration,
	shortPrincipalId,
} from "@/features/integrations/components/IntegrationFormatters";
import { IntegrationExecutionStatusBadge } from "@/features/integrations/components/IntegrationStatusBadge";
import {
	type IntegrationExecution,
	type IntegrationTrigger,
	isActiveExecutionStatus,
} from "@/features/integrations/models/IntegrationModels";

interface IntegrationExecutionTableProps {
	executions: readonly IntegrationExecution[];
	triggers: readonly IntegrationTrigger[];
	isCancelling: boolean;
	onView: (execution: IntegrationExecution) => void;
	onCancel: (execution: IntegrationExecution) => void;
}

/**
 * Execution rows, rendered in the order the server sent them (`ReceivedAtUtc DESC, Id DESC`, applied before the
 * window is taken). Nothing here sorts and no header is sortable: re-ordering would reorder the window rather than
 * the table, which reads as a sort over the whole history while showing only part of it.
 */
export function IntegrationExecutionTable({
	executions,
	triggers,
	isCancelling,
	onView,
	onCancel,
}: IntegrationExecutionTableProps) {
	const { t } = useTranslation();

	// The summary projection carries only the trigger id, so the name comes from the trigger list already on the page.
	// A trigger deleted after the run stays as its id rather than becoming blank.
	const triggerLabel = (id: string): string => triggers.find((trigger) => trigger.id === id)?.displayName ?? id;

	return (
		<ResponsiveTable
			rows={executions}
			rowKey={(execution) => execution.id}
			rowTestId={(execution) => `integration-execution-row-${execution.id}`}
			minWidth={1100}
			data-testid="integration-executions-table"
			empty={
				<EmptyState
					message={t("pages.integrations.executions.list.empty", "No executions match these filters yet.")}
					data-testid="integration-executions-empty"
				/>
			}
			columns={[
				{
					key: "trigger",
					header: t("pages.integrations.executions.list.columns.trigger", "Trigger"),
					primary: true,
					cell: (execution) => <Text fw={600}>{triggerLabel(execution.triggerId)}</Text>,
				},
				{
					key: "session",
					header: t("pages.integrations.executions.list.columns.session", "Session"),
					cell: (execution) => (
						<Tooltip label={execution.sessionId}>
							<Code title={execution.sessionId}>{shortPrincipalId(execution.sessionId)}</Code>
						</Tooltip>
					),
				},
				{
					key: "execution",
					header: t("pages.integrations.executions.list.columns.execution", "Execution"),
					cell: (execution) => (
						<Tooltip label={execution.id}>
							<Code title={execution.id}>{shortPrincipalId(execution.id)}</Code>
						</Tooltip>
					),
				},
				{
					key: "status",
					header: t("pages.integrations.executions.list.columns.status", "Status"),
					cell: (execution) => (
						<Group gap={4} wrap="nowrap">
							<IntegrationExecutionStatusBadge
								status={execution.status}
								data-testid={`integration-execution-status-${execution.id}`}
							/>
							{/* The category rides beside the status so an operator scanning the list can tell an
							    approval-required run from a queue-timeout without opening either dialog. */}
							{execution.failureCategory === null ? null : (
								<Badge variant="light" color="gray" data-testid={`integration-execution-category-${execution.id}`}>
									{formatFailureCategory(t, execution.failureCategory)}
								</Badge>
							)}
						</Group>
					),
				},
				{
					key: "received",
					header: t("pages.integrations.executions.list.columns.received", "Received"),
					cell: (execution) => <Text size="sm">{formatTimestamp(execution.receivedAtUtc)}</Text>,
				},
				{
					key: "started",
					header: t("pages.integrations.executions.list.columns.started", "Started"),
					cell: (execution) => <Text size="sm">{formatTimestamp(execution.startedAtUtc)}</Text>,
				},
				{
					key: "ended",
					header: t("pages.integrations.executions.list.columns.ended", "Ended"),
					cell: (execution) => <Text size="sm">{formatTimestamp(execution.endedAtUtc)}</Text>,
				},
				{
					key: "duration",
					header: t("pages.integrations.executions.list.columns.duration", "Duration"),
					cell: (execution) => <Text size="sm">{formatIntegrationDuration(execution.startedAtUtc, execution.endedAtUtc)}</Text>,
				},
				{
					key: "outputs",
					header: t("pages.integrations.executions.list.columns.outputs", "Outputs"),
					cell: (execution) => <Text size="sm">{execution.outputCount}</Text>,
				},
			]}
			actionsHeader={t("pages.integrations.executions.list.columns.actions", "Actions")}
			actions={(execution) => (
				<>
					<ActionIcon
						aria-label={t("pages.integrations.executions.list.viewAria", "View execution details")}
						variant="subtle"
						onClick={() => onView(execution)}
						data-testid={`integration-execution-view-${execution.id}`}
					>
						<IconEye size={16} />
					</ActionIcon>
					{isActiveExecutionStatus(execution.status) ? (
						<ActionIcon
							aria-label={t("pages.integrations.executions.list.cancelAria", "Cancel execution")}
							variant="subtle"
							color="red"
							disabled={isCancelling}
							onClick={() => onCancel(execution)}
							data-testid={`integration-execution-cancel-${execution.id}`}
						>
							<IconPlayerStop size={16} />
						</ActionIcon>
					) : null}
				</>
			)}
		/>
	);
}
