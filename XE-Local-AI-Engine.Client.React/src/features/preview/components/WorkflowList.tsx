import { ActionIcon, Group, Table, Text } from "@mantine/core";
import { IconPencil, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { PreviewWorkflowSummary } from "@/features/preview/models/PreviewWorkflowModels";

interface WorkflowListProps {
	workflows: readonly PreviewWorkflowSummary[];
	isMutating: boolean;
	onOpen: (id: string) => void;
	onDelete: (workflow: PreviewWorkflowSummary) => void;
}

// Table of saved preview workflows with open + delete row actions. Pure presentation — the parent owns the data
// and the action handlers. The graph is not part of the summary row (the list endpoint omits it), so the table
// shows only name + version + timestamps.
export function WorkflowList({ workflows, isMutating, onOpen, onDelete }: WorkflowListProps) {
	const { t } = useTranslation();

	if (workflows.length === 0) {
		return (
			<Text c="dimmed" data-testid="preview-workflows-empty">
				{t("pages.preview.list.empty", "No saved workflows yet. Create one to get started.")}
			</Text>
		);
	}

	return (
		<Table.ScrollContainer minWidth={640}>
			<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="preview-workflows-table">
				<Table.Thead>
					<Table.Tr>
						<Table.Th>{t("pages.preview.list.columns.name", "Name")}</Table.Th>
						<Table.Th>{t("pages.preview.list.columns.version", "Version")}</Table.Th>
						<Table.Th>{t("pages.preview.list.columns.updated", "Updated")}</Table.Th>
						<Table.Th>{t("pages.preview.list.columns.actions", "Actions")}</Table.Th>
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{workflows.map((workflow) => (
						<Table.Tr key={workflow.id} data-testid={`preview-workflow-row-${workflow.id}`}>
							<Table.Td>
								<Text fw={600}>{workflow.name}</Text>
							</Table.Td>
							<Table.Td>{workflow.version}</Table.Td>
							<Table.Td>
								<Text size="sm" c="dimmed">
									{new Date(workflow.updatedAtUtc).toLocaleString()}
								</Text>
							</Table.Td>
							<Table.Td>
								<Group gap="xs">
									<ActionIcon
										variant="subtle"
										aria-label={t("pages.preview.list.openAria", "Open workflow")}
										onClick={() => onOpen(workflow.id)}
										data-testid={`preview-workflow-open-${workflow.id}`}
									>
										<IconPencil size={16} />
									</ActionIcon>
									<ActionIcon
										variant="subtle"
										color="red"
										disabled={isMutating}
										aria-label={t("pages.preview.list.deleteAria", "Delete workflow")}
										onClick={() => onDelete(workflow)}
										data-testid={`preview-workflow-delete-${workflow.id}`}
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
