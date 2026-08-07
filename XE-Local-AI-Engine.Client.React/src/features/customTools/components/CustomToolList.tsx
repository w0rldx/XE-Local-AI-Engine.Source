import { ActionIcon, Badge, Group, Table, Text, Tooltip } from "@mantine/core";
import { IconAlertTriangle, IconPencil, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

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

	if (tools.length === 0) {
		return (
			<Text c="dimmed" data-testid="custom-tools-empty">
				{t("pages.customTools.list.empty", "No custom tools yet. Create one to give your agents a host command or HTTP call.")}
			</Text>
		);
	}

	return (
		<Table.ScrollContainer minWidth={720}>
			<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="custom-tools-table">
				<Table.Thead>
					<Table.Tr>
						<Table.Th>{t("pages.customTools.list.columns.name", "Name")}</Table.Th>
						<Table.Th>{t("pages.customTools.list.columns.kind", "Kind")}</Table.Th>
						<Table.Th>{t("pages.customTools.list.columns.enabled", "Enabled")}</Table.Th>
						<Table.Th>{t("pages.customTools.list.columns.version", "Version")}</Table.Th>
						<Table.Th>{t("pages.customTools.list.columns.actions", "Actions")}</Table.Th>
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{tools.map((tool) => (
						<Table.Tr key={tool.id} data-testid={`custom-tool-row-${tool.id}`}>
							<Table.Td>
								<Group gap="xs" wrap="nowrap">
									<Text fw={600} ff="monospace">
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
							</Table.Td>
							<Table.Td>
								<Badge variant="light" color={tool.kind === "Command" ? "orange" : "blue"}>
									{tool.kind === "Command"
										? t("pages.customTools.list.kindCommand", "Command")
										: t("pages.customTools.list.kindHttp", "HTTP fetch")}
								</Badge>
							</Table.Td>
							<Table.Td>
								<Badge variant="light" color={tool.enabled ? "teal" : "gray"}>
									{tool.enabled
										? t("pages.customTools.list.enabledBadge", "Enabled")
										: t("pages.customTools.list.disabledBadge", "Disabled")}
								</Badge>
							</Table.Td>
							<Table.Td>{tool.version}</Table.Td>
							<Table.Td>
								<Group gap="xs">
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
								</Group>
							</Table.Td>
						</Table.Tr>
					))}
				</Table.Tbody>
			</Table>
		</Table.ScrollContainer>
	);
}
