import { Badge, Group, Loader, Paper, Stack, Text } from "@mantine/core";
import { IconCalculator, IconClock } from "@tabler/icons-react";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";

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
	const { t } = useTranslation();

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
						{tool.requiresApproval
							? t("pages.tools.overview.requiresApproval", "requires approval")
							: t("pages.tools.overview.autoExecute", "auto-execute")}
					</Badge>
				</Group>
				<Text size="xs" c="dimmed">
					{tool.description}
				</Text>
			</Stack>
		</Paper>
	);
}

// Read-only view of what is INSTALLED on the node (dynamic tool-catalog): built-in in-process tools plus tools
// discovered from enabled MCP servers. It is deliberately NOT the per-turn tool offer — the backend narrows this
// catalog per invocation by the model's tool capability, the node's ToolCapableModels allow-list and the bound agent's
// AllowedToolNames (LocalToolOfferProvider). That gating is invocation-scoped and cannot be reproduced here, so the
// panel states the difference in copy rather than implying every listed tool is callable right now.
export function LocalToolsOverview() {
	const { t } = useTranslation();
	const catalogQuery = useToolCatalog();
	const tools = catalogQuery.data ?? [];

	return (
		<Paper withBorder={true} p="sm" data-testid="local-tools-overview">
			<Stack gap="xs">
				<Group justify="space-between" align="center">
					<Text size="sm" fw={600}>
						{t("pages.tools.overview.title", "Tools installed on this node")}
					</Text>
					{catalogQuery.data ? (
						<Badge size="xs" variant="dot" color="teal">
							{t("pages.tools.overview.installedCount", "{{total}} installed", { total: tools.length })}
						</Badge>
					) : null}
				</Group>

				<Text size="xs" c="dimmed" data-testid="local-tools-scope-notice">
					{t(
						"pages.tools.overview.scopeNotice",
						"The full node catalog. Which of these a chat turn can actually use is narrower: it depends on the selected model's tool support, the node's tool-capable model list, and the allowed tools of the agent handling the turn.",
					)}
				</Text>

				{catalogQuery.isLoading ? (
					<Group gap="sm" data-testid="local-tools-loading">
						<Loader size="sm" />
						<Text c="dimmed" size="sm">
							{t("pages.tools.overview.loading", "Loading tools…")}
						</Text>
					</Group>
				) : null}

				{catalogQuery.error ? (
					<Text size="sm" c="red" data-testid="local-tools-error">
						{t("pages.tools.overview.error", "Could not load the tool catalog.")}
					</Text>
				) : null}

				{!catalogQuery.isLoading && !catalogQuery.error && tools.length === 0 ? (
					<Text size="xs" c="dimmed" data-testid="local-tools-empty">
						{t("pages.tools.overview.empty", "No tools installed.")}
					</Text>
				) : null}

				{tools.map((tool) => (
					<LocalToolRow key={tool.name} tool={tool} />
				))}

				<Text size="xs" c="dimmed">
					{t(
						"pages.tools.overview.footer",
						"Built-in tools run in-process on this node. MCP tools run from their registered servers.",
					)}
				</Text>
			</Stack>
		</Paper>
	);
}
