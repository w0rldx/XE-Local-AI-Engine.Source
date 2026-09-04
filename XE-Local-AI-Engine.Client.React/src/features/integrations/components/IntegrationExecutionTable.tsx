import { ActionIcon, Badge, Code, Group, Table, Text, Tooltip } from "@mantine/core";
import { IconEye, IconPlayerStop } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import {
	formatIntegrationDuration,
	formatIntegrationOptionalTimestamp,
	formatIntegrationTimestamp,
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

	if (executions.length === 0) {
		return (
			<EmptyState
				message={t("pages.integrations.executions.list.empty", "No executions match these filters yet.")}
				data-testid="integration-executions-empty"
			/>
		);
	}

	// The summary projection carries only the trigger id, so the name comes from the trigger list already on the page.
	// A trigger deleted after the run stays as its id rather than becoming blank.
	const triggerLabel = (id: string): string => triggers.find((trigger) => trigger.id === id)?.displayName ?? id;

	return (
		<Table.ScrollContainer minWidth={1100}>
			<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="integration-executions-table">
				<Table.Thead>
					<Table.Tr>
						<Table.Th>{t("pages.integrations.executions.list.columns.trigger", "Trigger")}</Table.Th>
						<Table.Th>{t("pages.integrations.executions.list.columns.session", "Session")}</Table.Th>
						<Table.Th>{t("pages.integrations.executions.list.columns.execution", "Execution")}</Table.Th>
						<Table.Th>{t("pages.integrations.executions.list.columns.status", "Status")}</Table.Th>
						<Table.Th>{t("pages.integrations.executions.list.columns.received", "Received")}</Table.Th>
						<Table.Th>{t("pages.integrations.executions.list.columns.started", "Started")}</Table.Th>
						<Table.Th>{t("pages.integrations.executions.list.columns.ended", "Ended")}</Table.Th>
						<Table.Th>{t("pages.integrations.executions.list.columns.duration", "Duration")}</Table.Th>
						<Table.Th>{t("pages.integrations.executions.list.columns.outputs", "Outputs")}</Table.Th>
						<Table.Th>{t("pages.integrations.executions.list.columns.actions", "Actions")}</Table.Th>
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{executions.map((execution) => (
						<Table.Tr key={execution.id} data-testid={`integration-execution-row-${execution.id}`}>
							<Table.Td>
								<Text fw={600}>{triggerLabel(execution.triggerId)}</Text>
							</Table.Td>
							<Table.Td>
								<Tooltip label={execution.sessionId}>
									<Code title={execution.sessionId}>{shortPrincipalId(execution.sessionId)}</Code>
								</Tooltip>
							</Table.Td>
							<Table.Td>
								<Tooltip label={execution.id}>
									<Code title={execution.id}>{shortPrincipalId(execution.id)}</Code>
								</Tooltip>
							</Table.Td>
							<Table.Td>
								<Group gap={4} wrap="nowrap">
									<IntegrationExecutionStatusBadge
										status={execution.status}
										data-testid={`integration-execution-status-${execution.id}`}
									/>
									{/* The category rides beside the status so an operator scanning the list can tell an
									    approval-required run from a queue-timeout without opening either dialog. It is
									    rendered verbatim: a value this client does not know must still be readable. */}
									{execution.failureCategory === null ? null : (
										<Badge
											variant="light"
											color="gray"
											data-testid={`integration-execution-category-${execution.id}`}
										>
											{execution.failureCategory}
										</Badge>
									)}
								</Group>
							</Table.Td>
							<Table.Td>
								<Text size="sm">{formatIntegrationTimestamp(execution.receivedAtUtc)}</Text>
							</Table.Td>
							<Table.Td>
								<Text size="sm">{formatIntegrationOptionalTimestamp(execution.startedAtUtc)}</Text>
							</Table.Td>
							<Table.Td>
								<Text size="sm">{formatIntegrationOptionalTimestamp(execution.endedAtUtc)}</Text>
							</Table.Td>
							<Table.Td>
								<Text size="sm">{formatIntegrationDuration(execution.startedAtUtc, execution.endedAtUtc)}</Text>
							</Table.Td>
							<Table.Td>
								<Text size="sm">{execution.outputCount}</Text>
							</Table.Td>
							<Table.Td>
								<Group gap={4} wrap="nowrap">
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
								</Group>
							</Table.Td>
						</Table.Tr>
					))}
				</Table.Tbody>
			</Table>
		</Table.ScrollContainer>
	);
}
