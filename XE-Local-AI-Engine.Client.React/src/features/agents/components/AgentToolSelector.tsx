import { Alert, Badge, Checkbox, Group, Loader, Paper, Stack, Switch, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { ToolSourceBadge } from "@/features/tools/components/ToolSourceBadge";
import { type ToolCatalogEntry, toToolDisplayName } from "@/features/tools/models/ToolCatalogModels";
import { useToolCatalog } from "@/features/tools/queries/useToolCatalog";

interface AgentToolSelectorProps {
	// Selected tool names and the per-tool approval overrides. Both are owned by the parent form.
	selectedToolNames: readonly string[];
	toolApprovals: Readonly<Record<string, boolean>>;
	// When false the whole selector is disabled and a warning is shown (model not tool-capable).
	toolCapable: boolean;
	onToggleTool: (toolName: string, selected: boolean) => void;
	onToggleApproval: (toolName: string, requiresApproval: boolean) => void;
}

// Synthesize a catalog entry for a tool that is selected on the definition but no longer present in the live
// catalog (e.g. an MCP tool whose server was disabled/removed). It is shown so the user can still see and
// deselect it; it defaults to requiresApproval=true (the strict default) and an unknown source.
function unknownToolEntry(name: string): ToolCatalogEntry {
	return {
		name,
		description: "",
		requiresApproval: true,
		source: { kind: "builtin", serverSlug: null },
	};
}

// Tool multi-select with a per-tool approval toggle. The tool catalog is fetched live (useToolCatalog) — the
// SAME source the chat surface renders — so it includes built-ins and tools from enabled MCP servers. When the
// selected model is not tool-capable the selector is disabled and a warning is surfaced (no silent no-op).
export function AgentToolSelector({
	selectedToolNames,
	toolApprovals,
	toolCapable,
	onToggleTool,
	onToggleApproval,
}: AgentToolSelectorProps) {
	const { t } = useTranslation();
	const catalogQuery = useToolCatalog();

	// Render the live catalog plus any already-selected tools that are no longer in it (so they remain
	// deselectable). Selected-but-absent tools are appended after the catalog, in selection order.
	const rows = useMemo<ToolCatalogEntry[]>(() => {
		const catalog = catalogQuery.data ?? [];
		const catalogNames = new Set(catalog.map((tool) => tool.name));
		const orphanSelected = selectedToolNames
			.filter((name) => !catalogNames.has(name))
			.map((name) => unknownToolEntry(name));
		return [...catalog, ...orphanSelected];
	}, [catalogQuery.data, selectedToolNames]);

	return (
		<Stack gap="xs" data-testid="agent-tool-selector">
			<Text size="sm" fw={600}>
				{t("pages.agents.form.tools.label", "Tools")}
			</Text>
			{!toolCapable ? (
				<Alert color="yellow" icon={<IconAlertTriangle size={16} />} data-testid="agent-tool-capability-warning">
					{t(
						"pages.agents.form.tools.notCapableWarning",
						"The selected model is not tool-capable. Tool selection is disabled. Pick a tool-capable model to enable tools.",
					)}
				</Alert>
			) : null}

			{catalogQuery.isLoading ? (
				<Group gap="sm" data-testid="agent-tool-catalog-loading">
					<Loader size="sm" />
					<Text c="dimmed" size="sm">
						{t("pages.agents.form.tools.loading", "Loading tools…")}
					</Text>
				</Group>
			) : null}

			{catalogQuery.error ? (
				<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="agent-tool-catalog-error">
					{t("pages.agents.form.tools.loadError", "Could not load the tool catalog.")}
				</Alert>
			) : null}

			{!catalogQuery.isLoading && !catalogQuery.error && rows.length === 0 ? (
				<Text size="xs" c="dimmed" data-testid="agent-tool-catalog-empty">
					{t("pages.agents.form.tools.empty", "No tools available.")}
				</Text>
			) : null}

			{rows.map((tool) => {
				const isSelected = selectedToolNames.includes(tool.name);
				const requiresApproval = toolApprovals[tool.name] ?? tool.requiresApproval;

				return (
					<Paper withBorder={true} p="xs" key={tool.name} data-testid={`agent-tool-row-${tool.name}`}>
						<Stack gap={4}>
							<Group justify="space-between" align="center" wrap="nowrap">
								<Checkbox
									checked={isSelected}
									disabled={!toolCapable}
									label={
										<Group gap="xs" wrap="nowrap" align="center">
											<Text size="sm" fw={600} ff="monospace">
												{toToolDisplayName(tool.name)}
											</Text>
											<ToolSourceBadge source={tool.source} />
										</Group>
									}
									onChange={(event) => onToggleTool(tool.name, event.currentTarget.checked)}
									data-testid={`agent-tool-checkbox-${tool.name}`}
								/>
								<Switch
									size="sm"
									checked={requiresApproval}
									disabled={!toolCapable || !isSelected}
									label={
										<Badge size="xs" variant="light" color={requiresApproval ? "orange" : "teal"}>
											{requiresApproval
												? t("pages.agents.form.tools.requiresApproval", "requires approval")
												: t("pages.agents.form.tools.autoExecute", "auto-execute")}
										</Badge>
									}
									onChange={(event) => onToggleApproval(tool.name, event.currentTarget.checked)}
									data-testid={`agent-tool-approval-${tool.name}`}
								/>
							</Group>
							{tool.description ? (
								<Text size="xs" c="dimmed">
									{tool.description}
								</Text>
							) : null}
						</Stack>
					</Paper>
				);
			})}
		</Stack>
	);
}
