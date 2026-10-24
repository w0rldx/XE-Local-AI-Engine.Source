import { ActionIcon, Badge, Group, Text, Tooltip } from "@mantine/core";
import { IconAlertTriangle, IconPencil, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { ResponsiveTable } from "@/core/ui/components/ResponsiveTable/ResponsiveTable";
import { CUSTOM_TOOL_NAME_PREFIX, type CustomToolView } from "@/features/customTools/models/CustomToolModels";

interface CustomToolListProps {
	tools: readonly CustomToolView[];
	isMutating: boolean;
	onEdit: (id: string) => void;
	onDelete: (tool: CustomToolView) => void;
}

// Table of node custom tools with edit + delete row actions. Pure presentation — the parent owns data and handlers.
// The kind and enabled state ride as badges; a danger icon on every enabled tool keeps the host-exec risk visible in
// the list, not just in the editor.
export function CustomToolList({ tools, isMutating, onEdit, onDelete }: CustomToolListProps) {
	const { t } = useTranslation();

	return (
		<ResponsiveTable
			rows={tools}
			rowKey={(tool) => tool.id}
			rowTestId={(tool) => `custom-tool-row-${tool.id}`}
			minWidth={720}
			data-testid="custom-tools-table"
			empty={
				<EmptyState
					message={t(
						"pages.customTools.list.empty",
						"No custom tools yet. Create one to give your agents a host command or HTTP call.",
					)}
					data-testid="custom-tools-empty"
				/>
			}
			columns={[
				{
					key: "name",
					header: t("pages.customTools.list.columns.name", "Name"),
					primary: true,
					cell: (tool) => (
						<>
							<Group gap="xs">
								<Text fw={600} ff="monospace" style={{ overflowWrap: "anywhere" }}>
									{tool.name.startsWith(CUSTOM_TOOL_NAME_PREFIX) ? tool.name : `${CUSTOM_TOOL_NAME_PREFIX}${tool.name}`}
								</Text>
								<Badge variant="light" color="grape" size="sm">
									{tool.mode === "Parameterized"
										? t("pages.customTools.list.parameterizedBadge", "Parameterized")
										: t("pages.customTools.list.fixedBadge", "Fixed")}
								</Badge>
								{tool.enabled ? (
									<Tooltip
										multiline={true}
										w={280}
										label={t(
											"pages.customTools.list.dangerTooltip",
											"This tool is enabled and can run on the host when an agent calls it and you approve.",
										)}
									>
										<Badge
											variant="light"
											color="red"
											size="sm"
											leftSection={<IconAlertTriangle size={11} />}
											data-testid={`custom-tool-danger-${tool.id}`}
										>
											{t("pages.customTools.list.dangerBadge", "Host access")}
										</Badge>
									</Tooltip>
								) : null}
							</Group>
							{tool.description ? (
								<Text size="xs" c="dimmed" lineClamp={1}>
									{tool.description}
								</Text>
							) : null}
						</>
					),
				},
				{
					key: "kind",
					header: t("pages.customTools.list.columns.kind", "Kind"),
					cell: (tool) => (
						<Badge variant="light" color={tool.kind === "Command" ? "orange" : "blue"} style={{ minWidth: "max-content" }}>
							{tool.kind === "Command"
								? t("pages.customTools.list.kindCommand", "Command")
								: t("pages.customTools.list.kindHttp", "HTTP fetch")}
						</Badge>
					),
				},
				{
					key: "enabled",
					header: t("pages.customTools.list.columns.enabled", "Enabled"),
					cell: (tool) => (
						<Badge variant="light" color={tool.enabled ? "teal" : "gray"} style={{ minWidth: "max-content" }}>
							{tool.enabled
								? t("pages.customTools.list.enabledBadge", "Enabled")
								: t("pages.customTools.list.disabledBadge", "Disabled")}
						</Badge>
					),
				},
				{ key: "version", header: t("pages.customTools.list.columns.version", "Version"), cell: (tool) => tool.version },
			]}
			actionsHeader={t("pages.customTools.list.columns.actions", "Actions")}
			actions={(tool) => (
				<>
					<ActionIcon
						aria-label={t("pages.customTools.list.editAria", "Edit {{name}}", { name: tool.name })}
						variant="subtle"
						disabled={isMutating}
						onClick={() => onEdit(tool.id)}
						data-testid={`custom-tool-edit-${tool.id}`}
					>
						<IconPencil size={16} />
					</ActionIcon>
					<ActionIcon
						aria-label={t("pages.customTools.list.deleteAria", "Delete {{name}}", { name: tool.name })}
						variant="subtle"
						color="red"
						disabled={isMutating}
						onClick={() => onDelete(tool)}
						data-testid={`custom-tool-delete-${tool.id}`}
					>
						<IconTrash size={16} />
					</ActionIcon>
				</>
			)}
		/>
	);
}
