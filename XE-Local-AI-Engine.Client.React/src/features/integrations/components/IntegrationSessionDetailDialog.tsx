import { Alert, Code, Divider, Group, Stack, Table, Text, Tooltip } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import {
	formatIntegrationDuration,
	formatIntegrationTimestamp,
	shortPrincipalId,
} from "@/features/integrations/components/IntegrationFormatters";
import {
	IntegrationExecutionStatusBadge,
	IntegrationSessionStatusBadge,
} from "@/features/integrations/components/IntegrationStatusBadge";
import type { IntegrationSession } from "@/features/integrations/models/IntegrationModels";
import { useIntegrationExecutions } from "@/features/integrations/queries/useIntegrationExecutions";

interface IntegrationSessionDetailDialogProps {
	session: IntegrationSession;
	onClose: () => void;
}

/** The session record plus the executions that ran on it, read server-side by `sessionId`. */
export function IntegrationSessionDetailDialog({ session, onClose }: IntegrationSessionDetailDialogProps) {
	const { t } = useTranslation();

	const executionsQuery = useIntegrationExecutions({ sessionId: session.id });
	const executions = executionsQuery.data ?? [];

	const executionsError = executionsQuery.error
		? apiErrorMessage(
				executionsQuery.error,
				t("pages.integrations.sessions.errors.executions", "Could not load this session's executions."),
			)
		: undefined;

	return (
		<DialogShell
			opened={true}
			onClose={onClose}
			title={t("pages.integrations.sessions.detail.title", "Session detail")}
			size="xl"
			data-testid="integration-session-detail"
		>
			<Stack gap="sm">
				<Group gap="sm">
					<Text size="sm" c="dimmed" w={160}>
						{t("pages.integrations.sessions.list.columns.status", "Status")}
					</Text>
					<IntegrationSessionStatusBadge status={session.status} />
				</Group>
				<Group gap="sm">
					<Text size="sm" c="dimmed" w={160}>
						{t("pages.integrations.sessions.list.columns.id", "Session")}
					</Text>
					<Code>{session.id}</Code>
				</Group>
				<Group gap="sm">
					<Text size="sm" c="dimmed" w={160}>
						{t("pages.integrations.sessions.list.columns.trigger", "Trigger")}
					</Text>
					<Text size="sm">
						{session.triggerName === ""
							? t("pages.integrations.sessions.list.deletedTrigger", "Deleted trigger")
							: session.triggerName}
					</Text>
				</Group>
				<Group gap="sm">
					<Text size="sm" c="dimmed" w={160}>
						{t("pages.integrations.sessions.list.columns.principal", "Principal")}
					</Text>
					{/* Same shortened-with-tooltip form the executions detail uses, so one integrator reads the same on
					    both surfaces. The keys page is where this identity maps back to a credential. */}
					<Tooltip label={session.principalId}>
						<Code title={session.principalId} data-testid="integration-session-principal">
							{shortPrincipalId(session.principalId)}
						</Code>
					</Tooltip>
				</Group>
				<Group gap="sm">
					<Text size="sm" c="dimmed" w={160}>
						{t("pages.integrations.sessions.detail.agent", "Agent")}
					</Text>
					<Code>{session.agentDefinitionId}</Code>
				</Group>
				<Group gap="sm">
					<Text size="sm" c="dimmed" w={160}>
						{t("pages.integrations.sessions.list.columns.created", "Created")}
					</Text>
					<Text size="sm">{formatIntegrationTimestamp(session.createdAtUtc)}</Text>
				</Group>
				<Group gap="sm">
					<Text size="sm" c="dimmed" w={160}>
						{t("pages.integrations.sessions.list.columns.lastActivity", "Last activity")}
					</Text>
					<Text size="sm">{formatIntegrationTimestamp(session.lastActivityUtc)}</Text>
				</Group>

				<Divider
					label={t("pages.integrations.sessions.detail.executionsTitle", "Executions on this session")}
					labelPosition="left"
				/>

				{executionsError === undefined ? null : (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="integration-session-executions-error">
						{executionsError}
					</Alert>
				)}

				{executions.length === 0 ? (
					<EmptyState
						message={t("pages.integrations.sessions.detail.noExecutions", "No executions have run on this session.")}
						data-testid="integration-session-executions-empty"
					/>
				) : (
					<Table striped={true} verticalSpacing="xs" data-testid="integration-session-executions">
						<Table.Thead>
							<Table.Tr>
								<Table.Th>{t("pages.integrations.executions.list.columns.execution", "Execution")}</Table.Th>
								<Table.Th>{t("pages.integrations.executions.list.columns.status", "Status")}</Table.Th>
								<Table.Th>{t("pages.integrations.executions.list.columns.received", "Received")}</Table.Th>
								<Table.Th>{t("pages.integrations.executions.list.columns.duration", "Duration")}</Table.Th>
							</Table.Tr>
						</Table.Thead>
						<Table.Tbody>
							{executions.map((execution) => (
								<Table.Tr key={execution.id} data-testid={`integration-session-execution-${execution.id}`}>
									<Table.Td>
										<Code title={execution.id}>{shortPrincipalId(execution.id)}</Code>
									</Table.Td>
									<Table.Td>
										<IntegrationExecutionStatusBadge status={execution.status} />
									</Table.Td>
									<Table.Td>
										<Text size="sm">{formatIntegrationTimestamp(execution.receivedAtUtc)}</Text>
									</Table.Td>
									<Table.Td>
										<Text size="sm">
											{formatIntegrationDuration(execution.startedAtUtc, execution.endedAtUtc)}
										</Text>
									</Table.Td>
								</Table.Tr>
							))}
						</Table.Tbody>
					</Table>
				)}
			</Stack>
		</DialogShell>
	);
}
