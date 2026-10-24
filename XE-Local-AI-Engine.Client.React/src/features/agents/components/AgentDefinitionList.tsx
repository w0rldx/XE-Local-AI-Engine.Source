import { ActionIcon, Badge, Text } from "@mantine/core";
import { IconPencil, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { ResponsiveTable, type ResponsiveTableColumn } from "@/core/ui/components/ResponsiveTable/ResponsiveTable";
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

	const columns: readonly ResponsiveTableColumn<AgentDefinition>[] = [
		{
			key: "name",
			header: t("pages.agents.list.columns.name", "Name"),
			primary: true,
			cell: (definition) => (
				<>
					<Text fw={600}>
						{definition.isDefaultAssistant && definition.name === "Default Assistant"
							? t("pages.chat.defaultAgentName", "Default Assistant")
							: definition.name}
					</Text>
					{definition.description ? (
						<Text size="xs" c="dimmed" lineClamp={1}>
							{definition.description}
						</Text>
					) : null}
				</>
			),
		},
		{
			key: "kind",
			header: t("pages.agents.list.columns.kind", "Kind"),
			// The table's auto layout clipped the badge to "SING…"; the max-content floor keeps the whole label.
			cell: (definition) => (
				<Badge variant="light" color={definition.kind === "Orchestrator" ? "violet" : "blue"} style={{ minWidth: "max-content" }}>
					{t(`pages.agents.form.kind.options.${definition.kind}`, definition.kind)}
				</Badge>
			),
		},
		{
			key: "model",
			header: t("pages.agents.list.columns.model", "Model"),
			cell: (definition) => definition.modelProfile ?? t("pages.agents.list.nodeDefault", "Node default"),
		},
		{
			key: "tools",
			header: t("pages.agents.list.columns.tools", "Tools"),
			cell: (definition) =>
				definition.isDefaultAssistant ? t("pages.agents.list.allTools", "All") : definition.allowedToolNames.length,
		},
		{ key: "version", header: t("pages.agents.list.columns.version", "Version"), cell: (definition) => definition.version },
	];

	return (
		<ResponsiveTable
			rows={definitions}
			columns={columns}
			rowKey={(definition) => definition.id}
			rowTestId={(definition) => `agent-definition-row-${definition.id}`}
			minWidth={680}
			data-testid="agent-definitions-table"
			empty={
				<EmptyState
					message={t("pages.agents.list.empty", "No agent definitions yet. Create one to get started.")}
					data-testid="agent-definitions-empty"
				/>
			}
			actionsHeader={t("pages.agents.list.columns.actions", "Actions")}
			actions={(definition) => (
				<>
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
				</>
			)}
		/>
	);
}
