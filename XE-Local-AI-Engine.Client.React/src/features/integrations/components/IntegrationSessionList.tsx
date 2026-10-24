import { ActionIcon, Code, Text, Tooltip } from "@mantine/core";
import { IconEye, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { ResponsiveTable } from "@/core/ui/components/ResponsiveTable/ResponsiveTable";
import { shortPrincipalId } from "@/features/integrations/components/IntegrationFormatters";
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

	return (
		<ResponsiveTable
			rows={sessions}
			rowKey={(session) => session.id}
			rowTestId={(session) => `integration-session-row-${session.id}`}
			minWidth={900}
			data-testid="integration-sessions-table"
			empty={
				<EmptyState
					message={t("pages.integrations.sessions.list.empty", "No sessions match these filters yet.")}
					data-testid="integration-sessions-empty"
				/>
			}
			columns={[
				{
					key: "id",
					header: t("pages.integrations.sessions.list.columns.id", "Session"),
					cell: (session) => (
						<Tooltip label={session.id}>
							<Code title={session.id}>{shortPrincipalId(session.id)}</Code>
						</Tooltip>
					),
				},
				{
					key: "trigger",
					header: t("pages.integrations.sessions.list.columns.trigger", "Trigger"),
					primary: true,
					cell: (session) => (
						// Empty only when the trigger has been deleted — the session and its executions outlive it.
						<Text fw={600}>
							{session.triggerName === ""
								? t("pages.integrations.sessions.list.deletedTrigger", "Deleted trigger")
								: session.triggerName}
						</Text>
					),
				},
				{
					key: "principal",
					header: t("pages.integrations.sessions.list.columns.principal", "Principal"),
					// The OWNING integrator, rendered like every other principal on this surface: shortened, with the full
					// identity in the tooltip. Not the agent — a key rotation keeps the principal.
					cell: (session) => (
						<Tooltip label={session.principalId}>
							<Code data-testid={`integration-session-principal-${session.id}`} title={session.principalId}>
								{shortPrincipalId(session.principalId)}
							</Code>
						</Tooltip>
					),
				},
				{
					key: "status",
					header: t("pages.integrations.sessions.list.columns.status", "Status"),
					cell: (session) => (
						<IntegrationSessionStatusBadge status={session.status} data-testid={`integration-session-status-${session.id}`} />
					),
				},
				{
					key: "created",
					header: t("pages.integrations.sessions.list.columns.created", "Created"),
					cell: (session) => <Text size="sm">{formatTimestamp(session.createdAtUtc)}</Text>,
				},
				{
					key: "lastActivity",
					header: t("pages.integrations.sessions.list.columns.lastActivity", "Last activity"),
					cell: (session) => <Text size="sm">{formatTimestamp(session.lastActivityUtc)}</Text>,
				},
				{
					key: "executions",
					header: t("pages.integrations.sessions.list.columns.executions", "Executions"),
					cell: (session) => <Text size="sm">{session.executionCount}</Text>,
				},
			]}
			actionsHeader={t("pages.integrations.sessions.list.columns.actions", "Actions")}
			actions={(session) => (
				<>
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
				</>
			)}
		/>
	);
}
