import { ActionIcon, Code, Group, Table, Text, Tooltip } from "@mantine/core";
import { IconEye, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { formatIntegrationTimestamp, shortPrincipalId } from "@/features/integrations/components/IntegrationFormatters";
import { IntegrationSessionStatusBadge } from "@/features/integrations/components/IntegrationStatusBadge";
import type { IntegrationSession } from "@/features/integrations/models/IntegrationModels";

interface IntegrationSessionListProps {
	sessions: readonly IntegrationSession[];
	isMutating: boolean;
	onView: (session: IntegrationSession) => void;
	onDelete: (session: IntegrationSession) => void;
}

/**
 * Session rows in the order the server sent them (`LastActivityUtc DESC, Id DESC`, applied before the window is
 * taken). Nothing sorts here and no header is sortable, for the same reason the executions table has none.
 */
export function IntegrationSessionList({ sessions, isMutating, onView, onDelete }: IntegrationSessionListProps) {
	const { t } = useTranslation();

	if (sessions.length === 0) {
		return (
			<EmptyState
				message={t("pages.integrations.sessions.list.empty", "No sessions match these filters yet.")}
				data-testid="integration-sessions-empty"
			/>
		);
	}

	return (
		<Table.ScrollContainer minWidth={900}>
			<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="integration-sessions-table">
				<Table.Thead>
					<Table.Tr>
						<Table.Th>{t("pages.integrations.sessions.list.columns.id", "Session")}</Table.Th>
						<Table.Th>{t("pages.integrations.sessions.list.columns.trigger", "Trigger")}</Table.Th>
						<Table.Th>{t("pages.integrations.sessions.list.columns.status", "Status")}</Table.Th>
						<Table.Th>{t("pages.integrations.sessions.list.columns.created", "Created")}</Table.Th>
						<Table.Th>{t("pages.integrations.sessions.list.columns.lastActivity", "Last activity")}</Table.Th>
						<Table.Th>{t("pages.integrations.sessions.list.columns.executions", "Executions")}</Table.Th>
						<Table.Th>{t("pages.integrations.sessions.list.columns.actions", "Actions")}</Table.Th>
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{sessions.map((session) => (
						<Table.Tr key={session.id} data-testid={`integration-session-row-${session.id}`}>
							<Table.Td>
								<Tooltip label={session.id}>
									<Code title={session.id}>{shortPrincipalId(session.id)}</Code>
								</Tooltip>
							</Table.Td>
							<Table.Td>
								{/* Empty only when the trigger has been deleted — the session and its executions outlive it. */}
								<Text fw={600}>
									{session.triggerName === ""
										? t("pages.integrations.sessions.list.deletedTrigger", "Deleted trigger")
										: session.triggerName}
								</Text>
							</Table.Td>
							<Table.Td>
								<IntegrationSessionStatusBadge
									status={session.status}
									data-testid={`integration-session-status-${session.id}`}
								/>
							</Table.Td>
							<Table.Td>
								<Text size="sm">{formatIntegrationTimestamp(session.createdAtUtc)}</Text>
							</Table.Td>
							<Table.Td>
								<Text size="sm">{formatIntegrationTimestamp(session.lastActivityUtc)}</Text>
							</Table.Td>
							<Table.Td>
								<Text size="sm">{session.executionCount}</Text>
							</Table.Td>
							<Table.Td>
								<Group gap={4} wrap="nowrap">
									<ActionIcon
										aria-label={t("pages.integrations.sessions.list.viewAria", "View session details")}
										variant="subtle"
										onClick={() => onView(session)}
										data-testid={`integration-session-view-${session.id}`}
									>
										<IconEye size={16} />
									</ActionIcon>
									<ActionIcon
										aria-label={t("pages.integrations.sessions.list.deleteAria", "Delete session")}
										variant="subtle"
										color="red"
										disabled={isMutating}
										onClick={() => onDelete(session)}
										data-testid={`integration-session-delete-${session.id}`}
									>
										<IconTrash size={16} />
									</ActionIcon>
								</Group>
							</Table.Td>
						</Table.Tr>
					))}
				</Table.Tbody>
			</Table>
		</Table.ScrollContainer>
	);
}
