import { ActionIcon, Badge, Group, Table, Text } from "@mantine/core";
import { IconPencil, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import type { AgentDefinition } from "@/features/agents/models/AgentDefinitionModels";

interface AgentDefinitionListProps {
	definitions: readonly AgentDefinition[];
	isMutating: boolean;
	onEdit: (id: string) => void;
	onDelete: (definition: AgentDefinition) => void;
}

// Table of agent definitions with edit/delete row actions. Pure presentation — the parent owns the data and
// the action handlers.
export function AgentDefinitionList({ definitions, isMutating, onEdit, onDelete }: AgentDefinitionListProps) {
	const { t } = useTranslation();

	if (definitions.length === 0) {
		return (
			<EmptyState
				message={t("pages.agents.list.empty", "No agent definitions yet. Create one to get started.")}
				data-testid="agent-definitions-empty"
			/>
		);
	}

	return (
		<Table.ScrollContainer minWidth={680}>
			<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="agent-definitions-table">
				<Table.Thead>
					<Table.Tr>
						<Table.Th>{t("pages.agents.list.columns.name", "Name")}</Table.Th>
						<Table.Th>{t("pages.agents.list.columns.kind", "Kind")}</Table.Th>
						<Table.Th>{t("pages.agents.list.columns.model", "Model")}</Table.Th>
						<Table.Th>{t("pages.agents.list.columns.tools", "Tools")}</Table.Th>
						<Table.Th>{t("pages.agents.list.columns.version", "Version")}</Table.Th>
						<Table.Th>{t("pages.agents.list.columns.actions", "Actions")}</Table.Th>
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{definitions.map((definition) => (
						<Table.Tr key={definition.id} data-testid={`agent-definition-row-${definition.id}`}>
							<Table.Td>
								<Text fw={600}>{definition.name}</Text>
								{definition.description ? (
									<Text size="xs" c="dimmed" lineClamp={1}>
										{definition.description}
									</Text>
								) : null}
							</Table.Td>
							<Table.Td>
								<Badge variant="light" color={definition.kind === "Orchestrator" ? "violet" : "blue"}>
									{t(`pages.agents.form.kind.options.${definition.kind}`, definition.kind)}
								</Badge>
							</Table.Td>
							<Table.Td>
								{definition.modelProfile ?? t("pages.agents.list.nodeDefault", "Node default")}
							</Table.Td>
							<Table.Td>{definition.allowedToolNames.length}</Table.Td>
							<Table.Td>{definition.version}</Table.Td>
							<Table.Td>
								<Group gap="xs" wrap="nowrap">
									<ActionIcon
										aria-label={t("pages.agents.list.editAria", "Edit {{name}}", {
											name: definition.name,
										})}
										variant="subtle"
										disabled={isMutating}
										onClick={() => onEdit(definition.id)}
										data-testid={`agent-definition-edit-${definition.id}`}
									>
										<IconPencil size={16} />
									</ActionIcon>
									<ActionIcon
										aria-label={t("pages.agents.list.deleteAria", "Delete {{name}}", {
											name: definition.name,
										})}
										variant="subtle"
										color="red"
										disabled={isMutating}
										onClick={() => onDelete(definition)}
										data-testid={`agent-definition-delete-${definition.id}`}
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
