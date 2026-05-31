import { Badge, Group, Loader, Paper, Stack, Text } from "@mantine/core";
import { IconCalculator, IconClock } from "@tabler/icons-react";
import type { ReactNode } from "react";

import { ToolSourceBadge } from "@/features/tools/components/ToolSourceBadge";
import { type ToolCatalogEntry, toToolDisplayName } from "@/features/tools/models/ToolCatalogModels";
import { useToolCatalog } from "@/features/tools/queries/useToolCatalog";

function toolIcon(name: string): ReactNode {
	if (name === "GetCurrentTime") {
		return <IconClock size={14} />;
	}
	if (name === "Calculate") {
		return <IconCalculator size={14} />;
	}
	return null;
}

interface LocalToolRowProps {
	tool: ToolCatalogEntry;
}

function LocalToolRow({ tool }: LocalToolRowProps) {
	return (
		<Paper withBorder={true} p="xs" data-testid={`local-tool-row-${tool.name}`}>
			<Stack gap={4}>
				<Group gap="xs" wrap="nowrap" align="center">
					{toolIcon(tool.name)}
					<Text size="sm" fw={600} ff="monospace" style={{ flex: 1 }}>
						{toToolDisplayName(tool.name)}
					</Text>
					<ToolSourceBadge source={tool.source} />
					<Badge
						size="xs"
						variant="light"
						color={tool.requiresApproval ? "orange" : "teal"}
						data-testid={`local-tool-approval-badge-${tool.name}`}
					>
						{tool.requiresApproval ? "requires approval" : "auto-execute"}
					</Badge>
				</Group>
				<Text size="xs" c="dimmed">
					{tool.description}
				</Text>
			</Stack>
		</Paper>
	);
}

// Read-only overview of the node tool catalog (dynamic tool-catalog): built-in in-process tools plus tools discovered from
// enabled MCP servers. The catalog is fetched live (useToolCatalog) — it replaces the former static
// localToolCatalog const, so MCP tools appear/disappear with server enable/disable and each row shows its
// originating source (built-in vs a specific MCP server).
export function LocalToolsOverview() {
	const catalogQuery = useToolCatalog();
	const tools = catalogQuery.data ?? [];

	return (
		<Paper withBorder={true} p="sm" data-testid="local-tools-overview">
			<Stack gap="xs">
				<Group justify="space-between" align="center">
					<Text size="sm" fw={600}>
						Local tools
					</Text>
					{catalogQuery.data ? (
						<Badge size="xs" variant="dot" color="teal">
							{tools.length} available
						</Badge>
					) : null}
				</Group>

				{catalogQuery.isLoading ? (
					<Group gap="sm" data-testid="local-tools-loading">
						<Loader size="sm" />
						<Text c="dimmed" size="sm">
							Loading tools…
						</Text>
					</Group>
				) : null}

				{catalogQuery.error ? (
					<Text size="sm" c="red" data-testid="local-tools-error">
						Could not load the tool catalog.
					</Text>
				) : null}

				{!catalogQuery.isLoading && !catalogQuery.error && tools.length === 0 ? (
					<Text size="xs" c="dimmed" data-testid="local-tools-empty">
						No tools available.
					</Text>
				) : null}

				{tools.map((tool) => (
					<LocalToolRow key={tool.name} tool={tool} />
				))}

				<Text size="xs" c="dimmed">
					Built-in tools run in-process on this node. MCP tools run from their registered servers.
				</Text>
			</Stack>
		</Paper>
	);
}
