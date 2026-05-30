import { Alert, Badge, Checkbox, Group, Paper, Stack, Switch, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { localToolCatalog } from "@/features/chat/models/LocalToolCatalog";

interface AgentToolSelectorProps {
	// Selected tool names and the per-tool approval overrides. Both are owned by the parent form.
	selectedToolNames: readonly string[];
	toolApprovals: Readonly<Record<string, boolean>>;
	// When false the whole selector is disabled and a warning is shown (model not tool-capable).
	toolCapable: boolean;
	onToggleTool: (toolName: string, selected: boolean) => void;
	onToggleApproval: (toolName: string, requiresApproval: boolean) => void;
}

// Tool multi-select with a per-tool approval toggle. The tool catalog is the SAME static source the chat
// surface renders (LocalToolCatalog / LocalToolsOverview) — no second catalog. When the selected model is
// not tool-capable the selector is disabled and a warning is surfaced (no silent no-op).
export function AgentToolSelector({
	selectedToolNames,
	toolApprovals,
	toolCapable,
	onToggleTool,
	onToggleApproval,
}: AgentToolSelectorProps) {
	const { t } = useTranslation();

	return (
		<Stack gap="xs" data-testid="agent-tool-selector">
			<Text size="sm" fw={600}>
				{t("pages.agents.form.tools.label", "Tools")}
			</Text>
			{!toolCapable ? (
				<Alert
					color="yellow"
					icon={<IconAlertTriangle size={16} />}
					data-testid="agent-tool-capability-warning"
				>
					{t(
						"pages.agents.form.tools.notCapableWarning",
						"The selected model is not tool-capable. Tool selection is disabled. Pick a tool-capable model to enable tools.",
					)}
				</Alert>
			) : null}
			{localToolCatalog.map((tool) => {
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
										<Text size="sm" fw={600} ff="monospace">
											{tool.name}
										</Text>
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
							<Text size="xs" c="dimmed">
								{tool.description}
							</Text>
						</Stack>
					</Paper>
				);
			})}
		</Stack>
	);
}
