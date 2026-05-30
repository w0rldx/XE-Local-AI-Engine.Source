import { Alert, Badge, Group, Loader, Paper, Stack, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { useMcpServerTools } from "@/features/mcp/queries/useMcpServers";
import type { McpConnectionStatus } from "@/features/mcp/models/McpServerToolsModels";
import { toToolDisplayName } from "@/features/tools/models/ToolCatalogModels";

interface McpServerToolsPanelProps {
	// The server whose live discovered tools + connection status to show. Null collapses the panel and disables
	// the query so the page does not poke every server on list load.
	serverId: string | null;
}

function statusColor(status: McpConnectionStatus): string {
	if (status === "connected") {
		return "teal";
	}
	if (status === "error") {
		return "red";
	}
	if (status === "connecting") {
		// Amber for the transient in-progress state (enabled, refresh in flight) — distinct from the red
		// "error" and gray "disabled".
		return "yellow";
	}
	// "disabled" and any unknown status fall back to a neutral gray badge.
	return "gray";
}

// Live discovered-tools + connection-status view for one MCP server (loop P4 GetServerTools). Fetches on demand
// when a server row is expanded. A disabled server reports "disabled" with no tools; a failed connection reports
// "error" with a redacted message.
export function McpServerToolsPanel({ serverId }: McpServerToolsPanelProps) {
	const { t } = useTranslation();
	const toolsQuery = useMcpServerTools(serverId);

	if (serverId === null) {
		return null;
	}

	return (
		<Paper withBorder={true} p="sm" data-testid="mcp-server-tools-panel">
			<Stack gap="xs">
				<Group justify="space-between" align="center">
					<Text size="sm" fw={600}>
						{t("pages.mcp.tools.title", "Discovered tools")}
					</Text>
					{toolsQuery.data ? (
						<Badge size="xs" variant="light" color={statusColor(toolsQuery.data.status)}>
							{t(`pages.mcp.tools.status.${toolsQuery.data.status}`, toolsQuery.data.status)}
						</Badge>
					) : null}
				</Group>

				{toolsQuery.isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed" size="sm">
							{t("pages.mcp.tools.loading", "Loading discovered tools…")}
						</Text>
					</Group>
				) : null}

				{toolsQuery.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="mcp-server-tools-error">
						{t("pages.mcp.tools.loadError", "Could not load discovered tools.")}
					</Alert>
				) : null}

				{toolsQuery.data?.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="mcp-server-tools-connection-error">
						{toolsQuery.data.error}
					</Alert>
				) : null}

				{toolsQuery.data && !toolsQuery.data.error && toolsQuery.data.tools.length === 0 ? (
					<Text size="xs" c="dimmed" data-testid="mcp-server-tools-empty">
						{t("pages.mcp.tools.empty", "No tools discovered. Enable the server to connect and list its tools.")}
					</Text>
				) : null}

				{toolsQuery.data?.tools.map((tool) => (
					<Paper withBorder={true} p="xs" key={tool.name} data-testid={`mcp-discovered-tool-${tool.name}`}>
						<Stack gap={4}>
							<Group justify="space-between" align="center" wrap="nowrap">
								<Text size="sm" fw={600} ff="monospace" style={{ flex: 1 }}>
									{toToolDisplayName(tool.name)}
								</Text>
								<Badge size="xs" variant="light" color={tool.requiresApproval ? "orange" : "teal"}>
									{tool.requiresApproval
										? t("pages.mcp.tools.requiresApproval", "requires approval")
										: t("pages.mcp.tools.autoExecute", "auto-execute")}
								</Badge>
							</Group>
							{tool.description ? (
								<Text size="xs" c="dimmed">
									{tool.description}
								</Text>
							) : null}
						</Stack>
					</Paper>
				))}
			</Stack>
		</Paper>
	);
}
