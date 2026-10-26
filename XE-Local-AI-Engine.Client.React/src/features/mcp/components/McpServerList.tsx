import { ActionIcon, Badge, Loader, Stack, Switch, Text } from "@mantine/core";
import { IconPencil, IconRefresh, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { ResponsiveTable } from "@/core/ui/components/ResponsiveTable/ResponsiveTable";
import type { McpServerRegistration } from "@/features/mcp/models/McpServerModels";
import { statusColor } from "@/features/mcp/models/McpStatusModel";
import { useMcpServerTools } from "@/features/mcp/queries/useMcpServers";

interface McpServerListProps {
	servers: readonly McpServerRegistration[];
	isMutating: boolean;
	onEdit: (id: string) => void;
	onDelete: (server: McpServerRegistration) => void;
	onToggleEnabled: (server: McpServerRegistration, enabled: boolean) => void;
	onReconnect: (server: McpServerRegistration) => void;
}

// Table of registered MCP servers with enable/disable, edit, and delete row actions. Pure presentation — the
// parent owns the data and the action handlers. The enable switch is the strict-default gate surfaced per row:
// a server is registered disabled and only connects once the user flips it on here.
export function McpServerList({ servers, isMutating, onEdit, onDelete, onToggleEnabled, onReconnect }: McpServerListProps) {
	const { t } = useTranslation();

	return (
		<ResponsiveTable
			rows={servers}
			rowKey={(server) => server.id}
			rowTestId={(server) => `mcp-server-row-${server.id}`}
			minWidth={720}
			data-testid="mcp-servers-table"
			empty={
				<EmptyState
					message={t("pages.mcp.list.empty", "No MCP servers registered yet. Register one to get started.")}
					data-testid="mcp-servers-empty"
				/>
			}
			columns={[
				{
					key: "name",
					header: t("pages.mcp.list.columns.name", "Name"),
					primary: true,
					wrap: true,
					cell: (server) => (
						<>
							<Text fw={600}>{server.name}</Text>
							{server.description ? (
								<Text size="xs" c="dimmed" lineClamp={1} title={server.description}>
									{server.description}
								</Text>
							) : null}
						</>
					),
				},
				{
					key: "transport",
					header: t("pages.mcp.list.columns.transport", "Transport"),
					cell: (server) => (
						<Badge variant="light" color={server.transportKind === "Http" ? "grape" : "blue"}>
							{t(`pages.mcp.form.transport.options.${server.transportKind}`, server.transportKind)}
						</Badge>
					),
				},
				{
					key: "target",
					header: t("pages.mcp.list.columns.target", "Target"),
					wrap: true,
					cell: (server) => (
						<Text size="sm" ff="monospace">
							{server.transportKind === "Http" ? (server.url ?? "") : (server.command ?? "")}
						</Text>
					),
				},
				{
					key: "trust",
					header: t("pages.mcp.list.columns.trust", "Trust"),
					cell: (server) =>
						server.transportKind === "Stdio" ? (
							<Badge
								variant="light"
								color={server.trustTier === "Sandboxed" ? "teal" : "yellow"}
								data-testid={`mcp-server-trust-${server.id}`}
							>
								{t(`pages.mcp.form.trustTier.options.${server.trustTier}`, server.trustTier)}
							</Badge>
						) : (
							// The tier answers "where does the process run", and this node launches nothing for an
							// HTTP registration — showing a tier here would imply a grant nobody made.
							<Text size="sm" c="dimmed">
								{t("pages.mcp.list.trustNotApplicable", "n/a")}
							</Text>
						),
				},
				{
					key: "enabled",
					header: t("pages.mcp.list.columns.enabled", "Enabled"),
					cell: (server) => (
						<Switch
							size="sm"
							checked={server.enabled}
							disabled={isMutating}
							onChange={(event) => onToggleEnabled(server, event.currentTarget.checked)}
							aria-label={t("pages.mcp.list.enabledAria", "Toggle {{name}}", { name: server.name })}
							data-testid={`mcp-server-enabled-${server.id}`}
						/>
					),
				},
				{
					key: "status",
					header: t("pages.mcp.list.columns.status", "Status"),
					cell: (server) => <McpServerStatusCell server={server} />,
				},
				{ key: "version", header: t("pages.mcp.list.columns.version", "Version"), cell: (server) => server.version },
			]}
			actionsHeader={t("pages.mcp.list.columns.actions", "Actions")}
			actions={(server) => (
				<>
					{server.enabled ? (
						<ActionIcon
							aria-label={t("pages.mcp.list.reconnectAria", "Reconnect {{name}}", { name: server.name })}
							variant="subtle"
							disabled={isMutating}
							onClick={() => onReconnect(server)}
							data-testid={`mcp-server-reconnect-${server.id}`}
						>
							<IconRefresh size={16} />
						</ActionIcon>
					) : null}
					<ActionIcon
						aria-label={t("pages.mcp.list.editAria", "Edit {{name}}", { name: server.name })}
						variant="subtle"
						disabled={isMutating}
						onClick={() => onEdit(server.id)}
						data-testid={`mcp-server-edit-${server.id}`}
					>
						<IconPencil size={16} />
					</ActionIcon>
					<ActionIcon
						aria-label={t("pages.mcp.list.deleteAria", "Delete {{name}}", { name: server.name })}
						variant="subtle"
						color="red"
						disabled={isMutating}
						onClick={() => onDelete(server)}
						data-testid={`mcp-server-delete-${server.id}`}
					>
						<IconTrash size={16} />
					</ActionIcon>
				</>
			)}
		/>
	);
}

// Live connection state for one row, read from the same tools query the Inspect panel uses (so the two never disagree and
// share one cache entry). A disabled server is known to be disabled without asking the node.
function McpServerStatusCell({ server }: { server: McpServerRegistration }) {
	const { t } = useTranslation();
	const toolsQuery = useMcpServerTools(server.enabled ? server.id : null);

	if (!server.enabled) {
		return (
			<Badge size="sm" variant="light" color={statusColor("disabled")} data-testid={`mcp-server-status-${server.id}`}>
				{t("pages.mcp.tools.status.disabled", "disabled")}
			</Badge>
		);
	}

	if (!toolsQuery.data) {
		return toolsQuery.isLoading ? <Loader size="xs" /> : null;
	}

	const { status, failureReason } = toolsQuery.data;
	return (
		<Stack gap={2} maw={260}>
			<Badge size="sm" variant="light" color={statusColor(status)} data-testid={`mcp-server-status-${server.id}`}>
				{t(`pages.mcp.tools.status.${status}`, status)}
			</Badge>
			{status === "error" && failureReason ? (
				// The short reason only; the full message and any stderr tail are in Inspect.
				<Text size="xs" c="red" lineClamp={2} data-testid={`mcp-server-status-reason-${server.id}`}>
					{t(`pages.mcp.tools.failure.${failureReason}`)}
				</Text>
			) : null}
		</Stack>
	);
}
