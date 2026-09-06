// The saved workflow definitions, as a table. Pure presentation: the page owns the query, the confirmation and every
// mutation — this file only reports which row the operator picked. Same division as Preview's `WorkflowList`, which it
// is copy-adapted from (features never import each other).

import { ActionIcon, Alert, Button, Group, Loader, Stack, Table, Text } from "@mantine/core";
import { IconAlertTriangle, IconPlus, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import type { GraphWorkflowDefinitionSummaryResponse } from "@/features/graphWorkflows/models/GraphWorkflowModels";

export interface GraphWorkflowDefinitionListProps {
	readonly definitions: readonly GraphWorkflowDefinitionSummaryResponse[];
	readonly selectedId?: string;
	readonly isLoading?: boolean;
	readonly error?: unknown;
	readonly onSelect: (definitionId: string) => void;
	/** The page opens the meta dialog for a NEW definition. */
	readonly onCreate: () => void;
	/** The page confirms and mutates; the list just says which row. */
	readonly onDelete: (definitionId: string) => void;
}

export function GraphWorkflowDefinitionList({
	definitions,
	selectedId,
	isLoading = false,
	error,
	onSelect,
	onCreate,
	onDelete,
}: GraphWorkflowDefinitionListProps) {
	const { t } = useTranslation();

	return (
		<Stack gap="xs" data-testid="gw-definition-list">
			<Group justify="space-between" wrap="wrap" w="100%">
				<Text fw={600}>{t("pages.graphWorkflows.definitions.title", "Workflows")}</Text>
				<Button
					size="xs"
					variant="light"
					leftSection={<IconPlus size={14} />}
					onClick={onCreate}
					data-testid="gw-definition-create"
				>
					{t("pages.graphWorkflows.definitions.create", "New workflow")}
				</Button>
			</Group>

			{error !== undefined && error !== null ? (
				<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="gw-definition-list-error">
					{apiErrorMessage(error, t("pages.graphWorkflows.definitions.loadFailed", "Could not load the workflow list."))}
				</Alert>
			) : null}

			{isLoading ? <Loader size="sm" data-testid="gw-definition-list-loading" /> : null}

			{!isLoading && definitions.length === 0 ? (
				<EmptyState
					message={t("pages.graphWorkflows.definitions.empty", "No workflows yet. Create one to start authoring.")}
					data-testid="gw-definition-list-empty"
				/>
			) : null}

			{definitions.length > 0 ? (
				<Table.ScrollContainer minWidth={480}>
					<Table highlightOnHover={true} verticalSpacing="sm" data-testid="gw-definition-table">
						<Table.Thead>
							<Table.Tr>
								<Table.Th>{t("pages.graphWorkflows.definitions.columns.name", "Name")}</Table.Th>
								<Table.Th>{t("pages.graphWorkflows.definitions.columns.nodes", "Nodes")}</Table.Th>
								<Table.Th>{t("pages.graphWorkflows.definitions.columns.version", "Version")}</Table.Th>
								<Table.Th>{t("pages.graphWorkflows.definitions.columns.updated", "Updated")}</Table.Th>
								<Table.Th />
							</Table.Tr>
						</Table.Thead>
						<Table.Tbody>
							{definitions.map((definition) => {
								const id = definition.id ?? "";
								const name = definition.name ?? id;
								return (
									<Table.Tr
										key={id}
										bg={id === selectedId ? "var(--mantine-color-default-hover)" : undefined}
										data-testid={`gw-definition-row-${id}`}
									>
										<Table.Td>
											{/* The row's own control, not an onClick on the <tr>: a table row is not focusable and a
											    definition has to be reachable from the keyboard. */}
											<Button
												variant="subtle"
												size="compact-sm"
												px={0}
												onClick={() => onSelect(id)}
												data-testid={`gw-definition-open-${id}`}
											>
												{name}
											</Button>
											{definition.description ? (
												<Text size="xs" c="dimmed">
													{definition.description}
												</Text>
											) : null}
										</Table.Td>
										<Table.Td>{definition.nodeCount ?? 0}</Table.Td>
										<Table.Td>{definition.version ?? 1}</Table.Td>
										<Table.Td>
											<Text size="sm" c="dimmed">
												{formatTimestamp(definition.updatedAtUtc ?? null)}
											</Text>
										</Table.Td>
										<Table.Td>
											<ActionIcon
												variant="subtle"
												color="red"
												aria-label={t("pages.graphWorkflows.definitions.deleteAria", "Delete {{name}}", { name })}
												onClick={() => onDelete(id)}
												data-testid={`gw-definition-delete-${id}`}
											>
												<IconTrash size={16} />
											</ActionIcon>
										</Table.Td>
									</Table.Tr>
								);
							})}
						</Table.Tbody>
					</Table>
				</Table.ScrollContainer>
			) : null}
		</Stack>
	);
}
