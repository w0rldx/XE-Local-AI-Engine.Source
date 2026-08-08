import { ActionIcon, Badge, Group, Switch, Table, Text } from "@mantine/core";
import { IconPencil, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { McpServerRegistration } from "@/features/mcp/models/McpServerModels";

interface McpServerListProps {
	servers: readonly McpServerRegistration[];
	isMutating: boolean;
	onEdit: (id: string) => void;
	onDelete: (server: McpServerRegistration) => void;
	onToggleEnabled: (server: McpServerRegistration, enabled: boolean) => void;
}

// Table of registered MCP servers with enable/disable, edit, and delete row actions. Pure presentation — the
// parent owns the data and the action handlers. The enable switch is the strict-default gate surfaced per row:
// a server is registered disabled and only connects once the user flips it on here.
export function McpServerList({ servers, isMutating, onEdit, onDelete, onToggleEnabled }: McpServerListProps) {
	const { t } = useTranslation();

	if (servers.length === 0) {
		return (
			<Text c="dimmed" data-testid="mcp-servers-empty">
				{t("pages.mcp.list.empty", "No MCP servers registered yet. Register one to get started.")}
			</Text>
		);
	}

	return (
		<Table.ScrollContainer minWidth={720}>
			<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="mcp-servers-table">
				<Table.Thead>
					<Table.Tr>
						<Table.Th>{t("pages.mcp.list.columns.name", "Name")}</Table.Th>
						<Table.Th>{t("pages.mcp.list.columns.transport", "Transport")}</Table.Th>
						<Table.Th>{t("pages.mcp.list.columns.target", "Target")}</Table.Th>
						<Table.Th>{t("pages.mcp.list.columns.enabled", "Enabled")}</Table.Th>
						<Table.Th>{t("pages.mcp.list.columns.version", "Version")}</Table.Th>
						<Table.Th>{t("pages.mcp.list.columns.actions", "Actions")}</Table.Th>
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{servers.map((server) => (
						<Table.Tr key={server.id} data-testid={`mcp-server-row-${server.id}`}>
							<Table.Td>
								<Text fw={600}>{server.name}</Text>
								{server.description ? (
									<Text size="xs" c="dimmed" lineClamp={1}>
										{server.description}
									</Text>
								) : null}
							</Table.Td>
							<Table.Td>
								<Badge variant="light" color={server.transportKind === "Http" ? "grape" : "blue"}>
									{t(`pages.mcp.form.transport.options.${server.transportKind}`, server.transportKind)}
								</Badge>
							</Table.Td>
							<Table.Td>
								<Text size="sm" ff="monospace" lineClamp={1}>
									{server.transportKind === "Http" ? (server.url ?? "") : (server.command ?? "")}
								</Text>
							</Table.Td>
							<Table.Td>
								<Switch
									size="sm"
									checked={server.enabled}
									disabled={isMutating}
									onChange={(event) => onToggleEnabled(server, event.currentTarget.checked)}
									aria-label={t("pages.mcp.list.enabledAria", "Toggle {{name}}", { name: server.name })}
									data-testid={`mcp-server-enabled-${server.id}`}
								/>
							</Table.Td>
							<Table.Td>{server.version}</Table.Td>
							<Table.Td>
								<Group gap="xs">
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
								</Group>
							</Table.Td>
						</Table.Tr>
					))}
				</Table.Tbody>
			</Table>
		</Table.ScrollContainer>
	);
}
