// The saved workflow definitions, as compact card rows. Pure presentation: the page owns the query, the confirmation and every
// mutation — this file only reports which row the operator picked. Same division as Preview's `WorkflowList`, which it
// is copy-adapted from (features never import each other).

import { ActionIcon, Badge, Button, Group, Loader, Paper, Stack, Text, UnstyledButton } from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
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
				<InlineErrorAlert
					variant="light"
					message={apiErrorMessage(error, t("pages.graphWorkflows.definitions.loadFailed", "Could not load the workflow list."))}
					data-testid="gw-definition-list-error"
				/>
			) : null}

			{isLoading ? <Loader size="sm" data-testid="gw-definition-list-loading" /> : null}

			{!isLoading && definitions.length === 0 ? (
				<EmptyState
					message={t("pages.graphWorkflows.definitions.empty", "No workflows yet. Create one to start authoring.")}
					data-testid="gw-definition-list-empty"
				/>
			) : null}

			{definitions.length > 0 ? (
				// Card rows, not a table: the list lives in the fixed-width left rail, where five columns forced a horizontal
				// scrollbar. Same shape as the chat sidebar's conversation rows.
				<Stack gap={4} data-testid="gw-definition-rows">
					{definitions.map((definition) => {
						const id = definition.id ?? "";
						const name = definition.name ?? id;
						const selected = id === selectedId;
						return (
							<Paper
								key={id}
								p="xs"
								radius="md"
								withBorder={selected}
								bg={selected ? "var(--mantine-primary-color-light)" : "transparent"}
								data-testid={`gw-definition-row-${id}`}
								data-selected={selected ? "true" : "false"}
							>
								<Group gap={4} wrap="nowrap" align="flex-start">
									{/* The row's control is a real button so a definition is reachable from the keyboard; the delete
									    icon sits beside it, not inside it, so it stays its own focus stop. */}
									<UnstyledButton
										onClick={() => onSelect(id)}
										aria-current={selected ? "true" : undefined}
										style={{ flex: 1, minWidth: 0, textAlign: "inherit" }}
										data-testid={`gw-definition-open-${id}`}
									>
										<Group gap={6} wrap="nowrap">
											<Text fw={600} size="sm" truncate="end" title={name} data-testid={`gw-definition-name-${id}`}>
												{name}
											</Text>
											{definition.kind === "Chat" ? (
												<Badge size="xs" variant="light" style={{ flexShrink: 0 }} data-testid={`gw-definition-chat-badge-${id}`}>
													{t("pages.graphWorkflows.settings.kindOption.Chat", "Chat")}
												</Badge>
											) : null}
										</Group>
										{definition.description ? (
											<Text size="xs" c="dimmed" truncate="end" title={definition.description}>
												{definition.description}
											</Text>
										) : null}
										<Text
											size="xs"
											c="dimmed"
											truncate="end"
											title={formatTimestamp(definition.updatedAtUtc ?? null)}
											data-testid={`gw-definition-meta-${id}`}
										>
											{t("pages.graphWorkflows.definitions.meta", "{{count}} nodes · v{{version}} · updated {{updated}}", {
												count: definition.nodeCount ?? 0,
												version: definition.version ?? 1,
												updated: formatTimestamp(definition.updatedAtUtc ?? null, { month: "short", day: "numeric" }),
											})}
										</Text>
									</UnstyledButton>
									<ActionIcon
										variant="subtle"
										color="red"
										size="sm"
										aria-label={t("pages.graphWorkflows.definitions.deleteAria", "Delete {{name}}", { name })}
										onClick={() => onDelete(id)}
										data-testid={`gw-definition-delete-${id}`}
									>
										<IconTrash size={14} />
									</ActionIcon>
								</Group>
							</Paper>
						);
					})}
				</Stack>
			) : null}
		</Stack>
	);
}
